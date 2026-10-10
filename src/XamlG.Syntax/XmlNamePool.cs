namespace XamlG.Syntax;

/// <summary>Parse-local, bounded atom table. Hits compare slices of the original source without
/// allocating a temporary string. Neither arbitrary values nor other source snapshots are retained.</summary>
internal sealed class XmlNamePool
{
    // Bound both retained entries and adversarial hash-collision work. A document with
    // many unique names simply stops adding entries; previously interned names still hit.
    private const int MaximumNames = 512;
    private const int MaximumNameLength = 128;
    private readonly string _source;
    private readonly Dictionary<Key, string> _names;

    public XmlNamePool(string source)
    {
        _source = source;
        _names = new(new KeyComparer(source));
    }

    public string Get(int start, int length, int hash)
    {
        if (length == 0) return string.Empty;
        if (length > MaximumNameLength) return _source.Substring(start, length);
        var key = new Key(start, length, hash);
        if (_names.TryGetValue(key, out var value)) return value;
        value = _source.Substring(start, length);
        if (_names.Count < MaximumNames) _names.Add(key, value);
        return value;
    }

    private readonly struct Key(int start, int length, int hash)
    {
        public int Start { get; } = start;
        public int Length { get; } = length;
        public int Hash { get; } = hash;
    }

    private sealed class KeyComparer(string source) : IEqualityComparer<Key>
    {
        public bool Equals(Key left, Key right) => left.Length == right.Length &&
            (left.Start == right.Start || string.CompareOrdinal(source, left.Start, source, right.Start, left.Length) == 0);
        public int GetHashCode(Key value) => value.Hash;
    }
}
