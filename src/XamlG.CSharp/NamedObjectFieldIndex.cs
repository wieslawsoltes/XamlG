using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using XamlG.Compiler;

namespace XamlG.CSharp;

/// <summary>Emission-local reverse lookup. Bound records must be keyed by reference,
/// not recursive structural equality. Dense links preserve original field order
/// without a list/array allocation for every named object.</summary>
internal sealed class NamedObjectFieldIndex
{
    private readonly ImmutableArray<NamedObjectField> _fields;
    private readonly int[] _next;
    private readonly Dictionary<BoundObject, int> _heads;

    public NamedObjectFieldIndex(ImmutableArray<NamedObjectField> fields)
    {
        _fields = fields;
        _next = new int[fields.Length];
        _heads = new(fields.Length, ObjectIdentity.Instance);
        // Reverse insertion produces forward enumeration, including the unusual
        // case of non-contiguous aliases referring to the very same bound object.
        for (var i = fields.Length - 1; i >= 0; i--)
        {
            var owner = fields[i].Object;
            _next[i] = _heads.TryGetValue(owner, out var next) ? next : -1;
            _heads[owner] = i;
        }
    }

    public Enumerator For(BoundObject value) => new(_fields, _next, _heads.TryGetValue(value, out var first) ? first : -1);

    internal struct Enumerator
    {
        private readonly ImmutableArray<NamedObjectField> _fields;
        private readonly int[] _next;
        private int _position;
        private int _current;
        public Enumerator(ImmutableArray<NamedObjectField> fields, int[] next, int first)
        { _fields = fields; _next = next; _position = first; _current = -1; }
        public Enumerator GetEnumerator() => this;
        public NamedObjectField Current => _fields[_current];
        public bool MoveNext()
        {
            _current = _position;
            if (_current < 0) return false;
            _position = _next[_current];
            return true;
        }
    }

    private sealed class ObjectIdentity : IEqualityComparer<BoundObject>
    {
        public static readonly ObjectIdentity Instance = new();
        public bool Equals(BoundObject? left, BoundObject? right) => ReferenceEquals(left, right);
        public int GetHashCode(BoundObject value) => RuntimeHelpers.GetHashCode(value);
    }
}
