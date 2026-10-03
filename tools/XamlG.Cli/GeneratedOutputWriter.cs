using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XamlG.Tooling;

namespace XamlG.Cli;

/// <summary>Atomically replaces individual generated files and removes stale outputs only when their recorded content hash still matches.</summary>
internal static class GeneratedOutputWriter
{
    private const string ManifestName = ".xamlg-manifest.json";

    public static async Task WriteAsync(string directory, IReadOnlyList<XamlAnalysis> analyses, CancellationToken cancellationToken)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, ManifestName);
        var previous = System.IO.File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await System.IO.File.ReadAllTextAsync(manifestPath, cancellationToken)) ?? new()
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in previous.Keys) ValidateName(name);
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var analysis in analyses)
        {
            var name = analysis.Output.HintName;
            ValidateName(name);
            var path = Path.Combine(directory, name);
            var bytes = Encoding.UTF8.GetBytes(analysis.Output.Source);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!next.TryAdd(name, hash)) throw new InvalidOperationException("Two input documents produced the same output identity: " + name);
            if (System.IO.File.Exists(path))
            {
                var existing = Convert.ToHexString(SHA256.HashData(await System.IO.File.ReadAllBytesAsync(path, cancellationToken)));
                if (existing == hash) continue;
                if (!previous.TryGetValue(name, out var recorded) || existing != recorded)
                    throw new IOException("Refusing to overwrite an output modified outside XamlG: " + path);
            }
            await AtomicWriteAsync(path, bytes, cancellationToken);
        }
        foreach (var item in previous.Where(p => !next.ContainsKey(p.Key)))
        {
            var path = Path.Combine(directory, item.Key);
            if (!System.IO.File.Exists(path)) continue;
            var hash = Convert.ToHexString(SHA256.HashData(await System.IO.File.ReadAllBytesAsync(path, cancellationToken)));
            if (hash == item.Value) System.IO.File.Delete(path);
        }
        await AtomicWriteAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(next, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await System.IO.File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            System.IO.File.Move(temporary, path, overwrite: true);
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name.Contains('\\') || !name.EndsWith(".xaml.g.cs", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid generated output name in compiler manifest.");
    }
}
