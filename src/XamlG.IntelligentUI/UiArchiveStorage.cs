using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

public sealed record UiStoredArchive(string Version, string Json);

/// <summary>Private owner storage. Null expectedVersion means create-only; writes and deletes
/// must atomically compare the version. A stale browser tab must never overwrite a newer workspace.</summary>
public interface IUiArchiveStorage
{
    ValueTask<UiStoredArchive?> ReadAsync(string workspaceId, CancellationToken cancellationToken = default);
    ValueTask<string> WriteAsync(string workspaceId, string json, string? expectedVersion, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(string workspaceId, string expectedVersion, CancellationToken cancellationToken = default);
}

/// <summary>Explicit owner-driven persistence coordinator. Call Load before Save, after choosing
/// a trusted workspace. This class never subscribes to events or silently opts a user into storage.</summary>
public sealed class UiWorkspacePersistence(UiSessionStore store, IUiArchiveStorage storage, string workspaceId)
{
    private readonly SemaphoreSlim _gate = new(1);
    private string? _version;
    private bool _loaded;
    private long _savedGeneration = -1;
    public string WorkspaceId { get; } = workspaceId;
    public bool HasUnsavedChanges => !_loaded || store.Generation != _savedGeneration;
    public async ValueTask<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) throw new InvalidOperationException("This persistence session has already loaded its workspace.");
            var stored = await storage.ReadAsync(WorkspaceId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (stored != null)
            {
                store.RestoreArchive(stored.Json, WorkspaceId);
                _version = stored.Version; _savedGeneration = store.Generation;
            }
            _loaded = true;
            return stored != null;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded) throw new InvalidOperationException("Load the private workspace before saving; blind replacement is not permitted.");
            var generation = store.Generation;
            if (_savedGeneration == generation) return;
            var json = store.CaptureArchive(WorkspaceId);
            // A mutation during capture is conservatively saved again on the next call.
            var nextVersion = await storage.WriteAsync(WorkspaceId, json, _version, cancellationToken).ConfigureAwait(false);
            _version = nextVersion; _savedGeneration = generation;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask ForgetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded) throw new InvalidOperationException("Load the private workspace before forgetting it.");
            if (_version != null) await storage.DeleteAsync(WorkspaceId, _version, cancellationToken).ConfigureAwait(false);
            _version = null; _savedGeneration = -1;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Atomic same-directory replacement, exclusive per-workspace OS lock, and private
/// Unix files. The application must choose a trusted directory outside source control. This is
/// protection against concurrent writers, not against another process running as the same user.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("browser")]
public sealed class UiFileArchiveStorage : IUiArchiveStorage
{
    private readonly string _directory;
    public UiFileArchiveStorage(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (new DirectoryInfo(_directory).LinkTarget != null) throw new IOException("Archive directory must not be a symbolic link.");
    }
    public async ValueTask<UiStoredArchive?> ReadAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var path = FilePath(workspaceId);
        await using var lease = await LockAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<string> WriteAsync(string workspaceId, string json, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > UiWorkspaceArchive.MaximumBytes) throw new UiException("archive_limit", "Archive exceeds 16 MiB.");
        var path = FilePath(workspaceId);
        await using var lease = await LockAsync(path, cancellationToken).ConfigureAwait(false);
        var current = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (current?.Version != expectedVersion) throw new UiException("storage_conflict", "A newer UI workspace exists. Reload before replacing it.");
        var version = Guid.NewGuid().ToString("N"); var temporary = path + "." + version + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, Options(FileMode.CreateNew)))
            {
                await JsonSerializer.SerializeAsync(stream, new UiStoredArchive(version, json), AutomationJson.Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false); stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return version;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async ValueTask DeleteAsync(string workspaceId, string expectedVersion, CancellationToken cancellationToken = default)
    {
        var path = FilePath(workspaceId);
        await using var lease = await LockAsync(path, cancellationToken).ConfigureAwait(false);
        var current = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (current?.Version != expectedVersion) throw new UiException("storage_conflict", "The UI workspace changed before deletion.");
        cancellationToken.ThrowIfCancellationRequested(); File.Delete(path);
    }
    private string FilePath(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (workspaceId.Length > 256) throw new ArgumentOutOfRangeException(nameof(workspaceId));
        return Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId))) + ".json");
    }
    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        // The setter itself is platform-specific, even when assigned null.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
    private static async ValueTask<FileStream> LockAsync(string path, CancellationToken token)
    {
        var lockPath = path + ".lock";
        if (new FileInfo(lockPath).LinkTarget != null) throw new IOException("Archive lock must not be a symbolic link.");
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, Options(FileMode.OpenOrCreate)); }
            catch (IOException) when (attempt < 100) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }
    private static async ValueTask<UiStoredArchive?> ReadFileAsync(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget != null) throw new IOException("Archive file must not be a symbolic link.");
        if (!info.Exists) return null;
        // JSON-in-JSON can escape every UTF-16 character; bound the outer envelope too.
        if (info.Length > 6L * UiWorkspaceArchive.MaximumBytes + 1024) throw new UiException("archive_limit", "Stored envelope exceeds its bound.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var stored = await JsonSerializer.DeserializeAsync<UiStoredArchive>(stream, AutomationJson.Options, token).ConfigureAwait(false);
        if (stored == null || stored.Version.Length != 32 || !stored.Version.All(char.IsAsciiHexDigit) || Encoding.UTF8.GetByteCount(stored.Json) > UiWorkspaceArchive.MaximumBytes)
            throw new UiException("invalid_archive", "Invalid stored workspace envelope.");
        return stored;
    }
}
