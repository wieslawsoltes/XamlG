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

    // Only the decoder calls this path: its newly created dictionary has never
    // escaped and will never be mutated again. Public callers still receive the
    // defensive copy above; no mutable dictionary is exposed by the result.
    internal static XamlSourceInfo FromDecoded(string path, int start, int length, string? identity,
        string fingerprint, Dictionary<string, string> declarations, long version) =>
        new(path, start, length, identity, fingerprint, version, new ReadOnlyDictionary<string, string>(declarations));

    private XamlSourceInfo(string path, int start, int length, string? identity, string fingerprint,
        long version, ReadOnlyDictionary<string, string> declarations)
    {
        if (start < 0 || length < 0 || start > int.MaxValue - length) throw new ArgumentOutOfRangeException(nameof(start));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Start = start; Length = length; Identity = identity; Version = version;
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        Declarations = declarations;
    }
    public string Path { get; }
    public int Start { get; }
    public int Length { get; }
    public long Version { get; }
    public string? Identity { get; }
    public string Fingerprint { get; }
    public IReadOnlyDictionary<string, string> Declarations { get; }
}
