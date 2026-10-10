using System.Security.Cryptography;
using System.Text;
using XamlG.ProjectSystem;

namespace XamlG.Workspaces.Studio;

public sealed record WorkspaceDiskEntry(string Path, bool IsDirectory, long Length);
public sealed record WorkspaceDiskFile(string Path, string Hash, WorkspaceFile File, string Encoding);

/// <summary>
/// Root-confined owner file operations. Reparse points below the configured root are refused.
/// This is an I/O policy, not an OS sandbox for project code or hostile concurrent filesystem actors.
/// </summary>
public sealed class WorkspaceFileSystem
{
    public const int MaximumFileBytes = 3 * 1024 * 1024;
    private readonly SemaphoreSlim _writes = new(1, 1);
    public string Root { get; }
    public WorkspaceFileSystem(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = CanonicalRoot(root);
    }

    public string Resolve(string relativePath)
    {
        var path = WorkspacePath.Normalize(relativePath);
        var current = Root;
        RejectReparse(current);
        foreach (var part in path.Split('/'))
        {
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals(".hg", StringComparison.OrdinalIgnoreCase) || part.Equals(".svn", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Version-control internals are not exposed by the workspace file API.");
            current = Path.Combine(current, part);
            RejectReparse(current);
        }
        return current;
    }

    public string Relative(string absolutePath)
    {
        var relative = Path.GetRelativePath(Root, Path.GetFullPath(absolutePath));
        var normalized = WorkspacePath.Normalize(relative);
        _ = Resolve(normalized);
        return normalized;
    }

    public IReadOnlyList<WorkspaceDiskEntry> List(string? relativeDirectory = null, CancellationToken cancellationToken = default)
    {
        var root = relativeDirectory == null ? Root : Resolve(relativeDirectory);
        RejectReparse(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Workspace directory not found.");
        var result = new List<WorkspaceDiskEntry>(); var stack = new Stack<string>(); stack.Push(root);
        var inspected = 0;
        while (stack.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspected > VirtualWorkspace.MaximumFiles * 2) throw new InvalidOperationException("Workspace inventory limit exceeded; select a smaller root.");
                var name = Path.GetFileName(entry);
                if (name is ".git" or ".hg" or ".svn" or "bin" or "obj" or "node_modules" || name.StartsWith(".xamlg-", StringComparison.Ordinal)) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var path = Relative(entry);
                result.Add(new(path, isDirectory, isDirectory ? 0 : new FileInfo(entry).Length));
                if (result.Count > VirtualWorkspace.MaximumFiles) throw new InvalidOperationException("Workspace inventory limit exceeded; select a smaller root.");
                if (isDirectory) stack.Push(entry);
            }
        }
        return result.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }

    public async Task<WorkspaceDiskFile> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = WorkspacePath.Normalize(path);
        var bytes = await ReadBytesAsync(Resolve(path), cancellationToken).ConfigureAwait(false);
        var (file, encoding) = Decode(bytes);
        return new(path, Hash(bytes), file, encoding);
    }

    public async Task<WorkspaceDiskFile> WriteAsync(string path, string? expectedHash, WorkspaceFile file, string? encoding = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Content == null || file.Content.Length > VirtualWorkspace.MaximumFileCharacters) throw new ArgumentException("File content limit exceeded.");
        path = WorkspacePath.Normalize(path);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var target = Resolve(path);
            var previous = await CheckHashAsync(path, expectedHash, cancellationToken).ConfigureAwait(false);
            var format = encoding ?? previous?.Encoding ?? "utf-8";
            var bytes = Encode(file, format);
            if (bytes.Length > MaximumFileBytes) throw new ArgumentException("Native workspace file size limit exceeded.");
            var parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            target = Resolve(path);
            temporary = Path.Combine(parent, ".xamlg-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && previous != null) File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
            await CheckHashAsync(path, expectedHash, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: expectedHash != null);
            temporary = null;
            var (stored, storedEncoding) = Decode(bytes);
            return new(path, Hash(bytes), stored, storedEncoding);
        }
        finally
        {
            if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } }
            _writes.Release();
        }
    }

    public async Task DeleteAsync(string path, string expectedHash, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CheckHashAsync(path, expectedHash, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(Resolve(path));
        }
        finally { _writes.Release(); }
    }

    public async Task<WorkspaceDiskFile> MoveAsync(string path, string destination, string expectedHash, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CheckHashAsync(path, expectedHash, cancellationToken).ConfigureAwait(false);
            var target = Resolve(destination);
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException("The destination already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            target = Resolve(destination);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(Resolve(path), target);
            return await ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    public async Task<WorkspaceDiskFile?> CheckHashAsync(string path, string? expectedHash, CancellationToken cancellationToken)
    {
        var target = Resolve(path);
        if (!File.Exists(target))
        {
            if (expectedHash != null) throw new InvalidOperationException("File conflict: the file was removed.");
            if (Directory.Exists(target)) throw new InvalidOperationException("The target is a directory.");
            return null;
        }
        var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (expectedHash == null || !current.Hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File conflict: reload or save to a new path instead of overwriting external changes.");
        return current;
    }

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous);
        if (stream.Length > MaximumFileBytes) throw new IOException("Native workspace file size limit exceeded.");
        using var output = new MemoryStream(); var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaximumFileBytes) throw new IOException("Native workspace file size limit exceeded.");
            output.Write(buffer, 0, count);
        }
    }

    private static (WorkspaceFile File, string Encoding) Decode(byte[] bytes)
    {
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new(new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3)), "utf-8-bom");
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new(new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2)), "utf-16le");
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new(new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2)), "utf-16be");
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (!text.Contains('\0')) return (new(text), "utf-8");
        }
        catch (DecoderFallbackException) { }
        return (new(Convert.ToBase64String(bytes), true), "binary");
    }

    private static byte[] Encode(WorkspaceFile file, string encoding)
    {
        if (file.IsBinary) return Convert.FromBase64String(file.Content);
        Encoding format = encoding switch
        {
            "utf-8" or "binary" => new UTF8Encoding(false, true), "utf-8-bom" => new UTF8Encoding(true, true),
            "utf-16le" => new UnicodeEncoding(false, true, true), "utf-16be" => new UnicodeEncoding(true, true, true),
            _ => throw new ArgumentException("Unsupported text encoding.")
        };
        return [.. format.GetPreamble(), .. format.GetBytes(file.Content)];
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void RejectReparse(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Symlink/reparse traversal is not allowed in workspace file operations.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static string CanonicalRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Create the configured workspace root before starting the companion.");
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new UnauthorizedAccessException("The configured workspace root cannot be resolved.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
}
