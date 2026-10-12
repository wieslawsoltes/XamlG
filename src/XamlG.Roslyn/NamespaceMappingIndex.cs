using System.Collections.Immutable;

namespace XamlG.Roslyn;

/// <summary>Immutable ordinal index using the existing mapping array as its entry
/// table. Bucket chains preserve declaration order without copied keys/records.</summary>
internal sealed class NamespaceMappingIndex
{
    private readonly ImmutableArray<XmlNamespaceMapping> _mappings;
    private readonly int[] _heads;
    private readonly int[]? _next;

    public NamespaceMappingIndex(ImmutableArray<XmlNamespaceMapping> mappings)
    {
        _mappings = mappings;
        // Largest power of two <= M: average bucket occupancy is below two for
        // distributed hashes, and the bucket payload never exceeds 4*M bytes.
        // Dividing before shifting avoids overflow even for very large arrays.
        var capacity = 1;
        while (capacity <= mappings.Length / 2) capacity <<= 1;
        _heads = new int[capacity];
        int[]? next = null;
        // One-based links reserve zero for the end. Reverse insertion yields
        // forward declaration order, including interleaved groups/collisions.
        for (var i = mappings.Length - 1; i >= 0; i--)
        {
            var ns = mappings[i].XmlNamespace;
            // A null declared URI cannot match a successful request: the
            // resolver's existing StartsWith checks reject a null requested URI.
            if (ns == null) continue;
            var bucket = StringComparer.Ordinal.GetHashCode(ns) & (capacity - 1);
            var previous = _heads[bucket];
            if (previous != 0) (next ??= new int[mappings.Length])[i] = previous;
            _heads[bucket] = i + 1;
        }
        _next = next;
    }

    public Enumerator Match(string ns) => new(_mappings, _next, ns,
        _heads[StringComparer.Ordinal.GetHashCode(ns) & (_heads.Length - 1)]);

    internal struct Enumerator
    {
        private readonly ImmutableArray<XmlNamespaceMapping> _mappings;
        private readonly int[]? _next;
        private readonly string _namespace;
        private int _position;
        private int _current;
        public Enumerator(ImmutableArray<XmlNamespaceMapping> mappings, int[]? next, string ns, int head)
        { _mappings = mappings; _next = next; _namespace = ns; _position = head; _current = 0; }
        public Enumerator GetEnumerator() => this;
        public XmlNamespaceMapping Current => _mappings[_current - 1];
        public bool MoveNext()
        {
            while (_position != 0)
            {
                _current = _position;
                _position = _next == null ? 0 : _next[_current - 1];
                // Hashes select a bucket, never authorize a namespace match.
                if (_mappings[_current - 1].XmlNamespace == _namespace) return true;
            }
            return false;
        }
    }
}
