using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using XamlG.Agents;
using XamlG.Automation;

namespace XamlG.Studio.Host;

/// <summary>Exclusive, atomic private companion storage. Unix owner-only files; Windows current-user DPAPI.</summary>
internal sealed class StudioSessionStore : IDisposable
{
    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string _directory;
    private readonly FileStream _lock;
    private readonly SemaphoreSlim _io = new(1);
    public StudioSessionStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, FilePermissions | UnixFileMode.UserExecute);
        Check(_directory, true);
        _lock = Open(Path.Combine(_directory, "session.lock"), FileMode.OpenOrCreate);
    }
    public async Task<AgentSessionSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try { return await ReadAsync<AgentSessionSnapshot>("session", cancellationToken); }
        catch (Exception error) when (error is JsonException or InvalidDataException or CryptographicException)
        {
            var recovered = await ReadAsync<AgentSessionSnapshot>("session.previous", cancellationToken);
            if (recovered == null) throw;
            Console.Error.WriteLine("Recovered the last complete agent session. Inspect current source before resuming an interrupted task.");
            return recovered;
        }
    }
    public async Task SaveAsync(AgentSessionSnapshot snapshot, CancellationToken cancellationToken) => await WriteAsync("session", snapshot, true, cancellationToken);
    public async Task<string> TokenAsync(string variable)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        var saved = await ReadAsync<string>(variable, default);
        var value = configured ?? saved ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (value.Length is < 32 or > 256 || value.Any(char.IsWhiteSpace)) throw new InvalidOperationException(variable + " must contain 32–256 non-whitespace characters.");
        if (value != saved) await WriteAsync(variable, value, false, default);
        return value;
    }
    private async Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, name + ".dat");
        if (!File.Exists(path)) return default;
        Check(path, false);
        if (new FileInfo(path).Length > 96 * 1024 * 1024) throw new InvalidDataException("Saved companion state exceeds its limit.");
        var stored = await File.ReadAllBytesAsync(path, cancellationToken);
        var compressed = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser) : stored;
        using var input = new MemoryStream(compressed);
        using var unzip = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        try
        {
            int count;
            while ((count = await unzip.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (output.Length + count > 192 * 1024 * 1024) throw new InvalidDataException("Saved companion state exceeds its decompression limit.");
                output.Write(buffer, 0, count);
            }
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("Saved companion state is damaged.", error); }
        return JsonSerializer.Deserialize<T>(output.GetBuffer().AsSpan(0, (int)output.Length), AutomationJson.Options);
    }
    private async Task WriteAsync<T>(string name, T value, bool keepPrevious, CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken);
        string? temporary = null, previousTemporary = null;
        byte[]? plain = null;
        try
        {
            Check(_directory, true);
            plain = JsonSerializer.SerializeToUtf8Bytes(value, AutomationJson.Options);
            if (plain.Length > 192 * 1024 * 1024) throw new InvalidDataException("Companion session exceeds its storage limit.");
            using var compressed = new MemoryStream();
            await using (var zip = new BrotliStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) await zip.WriteAsync(plain, cancellationToken);
            var bytes = compressed.ToArray();
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            var path = Path.Combine(_directory, name + ".dat"); temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var output = Open(temporary, FileMode.CreateNew))
            { await output.WriteAsync(bytes, cancellationToken); await output.FlushAsync(cancellationToken); output.Flush(flushToDisk: true); }
            if (File.Exists(path))
            {
                Check(path, false);
                if (keepPrevious)
                {
                    var previous = Path.Combine(_directory, name + ".previous.dat");
                    if (File.Exists(previous)) Check(previous, false);
                    previousTemporary = previous + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(path, previousTemporary);
                    File.Move(previousTemporary, previous, overwrite: true); previousTemporary = null;
                }
            }
            File.Move(temporary, path, overwrite: true); temporary = null;
        }
        finally
        {
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            if (temporary != null) File.Delete(temporary);
            if (previousTemporary != null) File.Delete(previousTemporary);
            _io.Release();
        }
    }
    private static FileStream Open(string path, FileMode mode)
    {
        if (File.Exists(path)) Check(path, false);
        var options = new FileStreamOptions { Access = FileAccess.ReadWrite, Mode = mode, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FilePermissions;
        return new(path, options);
    }
    private static void Check(string path, bool directory)
    {
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget != null) throw new IOException("Private Studio storage cannot use symbolic links.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Private Studio storage requires owner-only permissions.");
    }
    public void Dispose() { _lock.Dispose(); _io.Dispose(); }
}
