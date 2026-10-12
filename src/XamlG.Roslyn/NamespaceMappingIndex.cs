using System.Collections.Immutable;

namespace XamlG.Roslyn;

/// <summary>Immutable ordinal index into the original mapping array. Groups keep
/// declaration order without per-namespace lists or copied mapping records.</summary>
internal sealed class NamespaceMappingIndex
{
    private readonly ImmutableArray<XmlNamespaceMapping> _mappings;
    private readonly Dictionary<string, int> _heads = new(StringComparer.Ordinal);
    private readonly int[]? _next;

    public NamespaceMappingIndex(ImmutableArray<XmlNamespaceMapping> mappings)
    {
        _mappings = mappings;
        int[]? next = null;
        // One-based links reserve zero for the end. Reverse insertion yields
        // forward declaration order even for interleaved namespace groups.
        for (var i = mappings.Length - 1; i >= 0; i--)
        {
            var ns = mappings[i].XmlNamespace;
            // A null URI cannot match a successful ResolveNamespaceTargets call:
            // its existing StartsWith checks reject a null requested namespace.
            if (ns == null) continue;
            if (_heads.TryGetValue(ns, out var previous))
                (next ??= new int[mappings.Length])[i] = previous;
            _heads[ns] = i + 1;
        }
        _next = next;
    }

    public Enumerator Match(string ns) => new(_mappings, _next, _heads.TryGetValue(ns, out var head) ? head : 0);

    internal struct Enumerator
    {
        private readonly ImmutableArray<XmlNamespaceMapping> _mappings;
        private readonly int[]? _next;
        private int _position;
        private int _current;
        public Enumerator(ImmutableArray<XmlNamespaceMapping> mappings, int[]? next, int head)
        { _mappings = mappings; _next = next; _position = head; _current = 0; }
        public Enumerator GetEnumerator() => this;
        public XmlNamespaceMapping Current => _mappings[_current - 1];
        public bool MoveNext()
        {
            _current = _position;
            if (_current == 0) return false;
            _position = _next == null ? 0 : _next[_current - 1];
            return true;
        }
    }
}
