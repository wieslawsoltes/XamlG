using System.Collections.ObjectModel;

namespace XamlG.Runtime;

/// <summary>Compiler-emitted UTF-16 source coordinates, revision and declaration identities.</summary>
public sealed class XamlSourceInfo
{
    public XamlSourceInfo(string path, int start, int length, string? identity, string fingerprint,
        IReadOnlyDictionary<string, string>? declarations = null, long version = 0)
    {
        if (start < 0 || length < 0 || start > int.MaxValue - length) throw new ArgumentOutOfRangeException(nameof(start));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Start = start; Length = length; Identity = identity; Version = version;
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        if (declarations != null) foreach (var item in declarations) copy.Add(item.Key, item.Value);
        Declarations = new ReadOnlyDictionary<string, string>(copy);
    }
    public string Path { get; }
    public int Start { get; }
    public int Length { get; }
    public long Version { get; }
    public string? Identity { get; }
    public string Fingerprint { get; }
    public IReadOnlyDictionary<string, string> Declarations { get; }
}
