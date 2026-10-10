using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace XamlG.ProjectSystem;

/// <summary>ZIP interchange operates on memory only; archive paths are never extracted to disk.</summary>
public static class WorkspaceArchive
{
    public const int MaximumArchiveBytes = 64 * 1024 * 1024;
    public const int MaximumEntryBytes = 3 * 1024 * 1024;
    public const int MaximumExpandedBytes = 32 * 1024 * 1024;

    public static async Task<VirtualWorkspace> ImportAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var bounded = new MemoryStream();
        await CopyBoundedAsync(source, bounded, MaximumArchiveBytes, cancellationToken).ConfigureAwait(false);
        bounded.Position = 0;
        using var archive = new ZipArchive(bounded, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > VirtualWorkspace.MaximumFiles) throw new InvalidDataException("Archive entry limit exceeded.");
        var files = new Dictionary<string, WorkspaceFile>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Symbolic links are not imported from workspace archives.");
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var path = WorkspacePath.Normalize(directory ? entry.FullName.TrimEnd('/', '\\') : entry.FullName);
            if (directory) continue;
            if (entry.Length > MaximumEntryBytes || (expanded += entry.Length) > MaximumExpandedBytes) throw new InvalidDataException("Expanded archive content limit exceeded.");
            await using var stream = entry.Open();
            using var contents = new MemoryStream();
            await CopyBoundedAsync(stream, contents, MaximumEntryBytes, cancellationToken).ConfigureAwait(false);
            if (contents.Length != entry.Length) throw new InvalidDataException("The archive entry length does not match its content.");
            if (!files.TryAdd(path, Decode(contents.ToArray()))) throw new InvalidDataException("Duplicate archive path: " + path);
        }
        return new(files);
    }

    public static async Task<byte[]> ExportAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = new VirtualWorkspace(snapshot.Files, snapshot.EntryPath);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            long expanded = 0;
            foreach (var (path, file) in snapshot.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = Encode(file);
                if (bytes.Length > MaximumEntryBytes || (expanded += bytes.Length) > MaximumExpandedBytes) throw new InvalidDataException("Export content limit exceeded.");
                var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var stream = entry.Open();
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        if (output.Length > MaximumArchiveBytes) throw new InvalidDataException("Export archive limit exceeded.");
        return output.ToArray();
    }

    public static WorkspaceFile Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumEntryBytes) throw new InvalidDataException("Workspace file size limit exceeded.");
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return new(new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3));
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return new(new UTF32Encoding(false, false, true).GetString(bytes, 4, bytes.Length - 4));
            if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return new(new UTF32Encoding(true, false, true).GetString(bytes, 4, bytes.Length - 4));
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return new(new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2));
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return new(new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2));
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (!text.Contains('\0')) return new(text);
        }
        catch (DecoderFallbackException) { }
        return new(Convert.ToBase64String(bytes), true);
    }

    public static byte[] Encode(WorkspaceFile file)
    {
        if (file.IsBinary) return Convert.FromBase64String(file.Content);
        // Imported text is normalized to UTF-8 except when an XML declaration requires a Unicode
        // encoding. Binary assets retain their original bytes. Native saves preserve their disk encoding.
        var declaration = Regex.Match(file.Content[..Math.Min(file.Content.Length, 256)],
            "^\\s*<\\?xml\\s+[^?]*encoding\\s*=\\s*['\"](?<encoding>[^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        Encoding encoding = declaration.Groups["encoding"].Value.ToLowerInvariant() switch
        {
            "utf-16" or "utf-16le" => new UnicodeEncoding(false, true, true),
            "utf-16be" => new UnicodeEncoding(true, true, true),
            "utf-32" or "utf-32le" => new UTF32Encoding(false, true, true),
            "utf-32be" => new UTF32Encoding(true, true, true),
            "" or "utf-8" => new UTF8Encoding(false, true),
            _ => throw new InvalidDataException("Export supports UTF-8/16/32 XML declarations. Convert the file explicitly before exporting another declared encoding.")
        };
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(file.Content)];
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, int limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024]; var total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return;
            if ((total += count) > limit) throw new InvalidDataException("Workspace stream limit exceeded.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }
}
