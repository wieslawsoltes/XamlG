using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XamlG.CSharp.Integration;

namespace XamlG.Cli;

/// <summary>Writes compiler-owned files only. Validates every overwrite before mutation and
/// removes stale outputs only when their recorded content hash still matches.</summary>
internal static class GeneratedOutputWriter
{
    private const string ManifestName = ".xamlg-manifest.json";

    public static async Task WriteAsync(string directory, IEnumerable<XamlGeneratedSource> sources, CancellationToken cancellationToken)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, ManifestName);
        var previous = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(manifestPath, cancellationToken)) ?? new()
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in previous.Keys) ValidateName(name);
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        var writes = new List<(string Path, byte[] Bytes)>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = source.HintName;
            ValidateName(name);
            var path = Path.Combine(directory, name);
            var bytes = Encoding.UTF8.GetBytes(source.Source);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!next.TryAdd(name, hash)) throw new InvalidOperationException("Two sources produced the same output identity: " + name);
            if (File.Exists(path))
            {
                var existing = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken)));
                if (existing == hash) continue;
                if (!previous.TryGetValue(name, out var recorded) || existing != recorded)
                    throw new IOException("Refusing to overwrite an output modified outside XamlG: " + path);
            }
            writes.Add((path, bytes));
        }
        foreach (var write in writes) await AtomicWriteAsync(write.Path, write.Bytes, cancellationToken);
        foreach (var item in previous.Where(p => !next.ContainsKey(p.Key)))
        {
            var path = Path.Combine(directory, item.Key);
            if (!File.Exists(path)) continue;
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken)));
            if (hash == item.Value) File.Delete(path);
        }
        await AtomicWriteAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(next, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name.Contains('\\') || name.Contains('/') ||
            !(name.EndsWith(".xaml.g.cs", StringComparison.Ordinal) || name == XamlSourceIntegrationResult.HintName))
            throw new InvalidDataException("Invalid generated output name in compiler manifest.");
    }
}
