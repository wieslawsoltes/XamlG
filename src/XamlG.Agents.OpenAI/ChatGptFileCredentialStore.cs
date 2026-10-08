using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XamlG.Agents.OpenAI;

/// <summary>Lifetime-exclusive host store. Unix files use 0600 in a 0700 directory; Windows
/// encrypts the entire snapshot with current-user DPAPI. Tokens are persisted only on opt-in.</summary>
public sealed class ChatGptFileCredentialStore : IChatGptCredentialStore
{
    private static readonly UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UnixFileMode DirectoryModeBits = FileModeBits | UnixFileMode.UserExecute;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private readonly string _path;
    private readonly FileStream _lock;
    private readonly SemaphoreSlim _io = new(1);
    public ChatGptFileCredentialStore(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, DirectoryModeBits);
        CheckPath(directory, directory: true);
        _path = Path.Combine(directory, "accounts.dat");
        var lockPath = Path.Combine(directory, "accounts.lock");
        if (File.Exists(lockPath)) CheckPath(lockPath, directory: false);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
        _lock = new(lockPath, options);
    }
    public async Task<ChatGptStoredState?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _io.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return null;
            CheckPath(_path, directory: false);
            if (new FileInfo(_path).Length > 2 * 1024 * 1024) throw new ChatGptAccountException("credential_store_too_large");
            var stored = await File.ReadAllBytesAsync(_path, cancellationToken);
            var plain = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser) : stored;
            try { return JsonSerializer.Deserialize<ChatGptStoredState>(plain, Json) ?? throw new ChatGptAccountException("invalid_credential_store"); }
            finally { CryptographicOperations.ZeroMemory(plain); if (!ReferenceEquals(plain, stored)) CryptographicOperations.ZeroMemory(stored); }
        }
        finally { _io.Release(); }
    }
    public async Task WriteAsync(ChatGptStoredState state, CancellationToken cancellationToken = default)
    {
        await _io.WaitAsync(cancellationToken);
        string? temporary = null; byte[]? plain = null, stored = null;
        try
        {
            CheckPath(Path.GetDirectoryName(_path)!, directory: true);
            if (File.Exists(_path)) CheckPath(_path, directory: false);
            var persisted = new ChatGptStoredState { HostId = state.HostId, AuthenticationOrigin = state.AuthenticationOrigin, ApiEndpoint = state.ApiEndpoint, ActiveAccountId = state.ActiveAccountId,
                Accounts = state.Accounts.Select(account => new ChatGptStoredAccount { Id = account.Id, Label = account.Label,
                    ClientId = account.ClientId, Subject = account.Subject, Email = account.Email, Remember = account.Remember,
                    Tokens = account.Remember ? account.Tokens : null }).ToArray() };
            plain = JsonSerializer.SerializeToUtf8Bytes(persisted, Json);
            if (plain.Length > 2 * 1024 * 1024) throw new ChatGptAccountException("credential_store_too_large");
            stored = OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser) : plain;
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
            await using (var file = new FileStream(temporary, options))
            { await file.WriteAsync(stored, cancellationToken); file.Flush(flushToDisk: true); }
            File.Move(temporary, _path, overwrite: true); temporary = null;
        }
        finally
        {
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            if (stored != null && !ReferenceEquals(plain, stored)) CryptographicOperations.ZeroMemory(stored);
            if (temporary != null) File.Delete(temporary);
            _io.Release();
        }
    }
    private static void CheckPath(string path, bool directory)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ChatGptAccountException("credential_store_links_rejected");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~(directory ? DirectoryModeBits : FileModeBits)) != 0)
            throw new ChatGptAccountException("credential_store_requires_owner_only_permissions");
    }
    public void Dispose() { _lock.Dispose(); _io.Dispose(); }
}
