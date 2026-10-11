using XamlG.Compiler.Resources;

namespace XamlG.CSharp.Resources;

/// <summary>Build-local ordered reverse adjacency. One geometrically grown edge
/// buffer replaces a List and its backing array for every referenced document.
/// Append order is preserved: it determines which competing failure is reported.</summary>
internal sealed class ResourceReverseEdges
{
    private readonly Endpoints[] _targets;
    private Entry[] _entries = Array.Empty<Entry>();
    private int _count;

    public ResourceReverseEdges(int documentCount)
    {
        if (documentCount < 0) throw new ArgumentOutOfRangeException(nameof(documentCount));
        _targets = new Endpoints[documentCount];
    }

    public void Add(int target, int parent, BoundResourceExpression edge)
    {
        // Links are one-based so default-zero target slots need no initialization
        // pass. No user callbacks run between reservation and link publication.
        ref var endpoints = ref _targets[target];
        if (_count == _entries.Length) Grow();
        _entries[_count] = new(parent, edge);
        var link = ++_count;
        if (endpoints.Last == 0) endpoints.First = link;
        else _entries[endpoints.Last - 1].Next = link;
        endpoints.Last = link;
    }

    // Enumeration is used only after building is complete. It owns no mutable
    // project state and allocates neither an iterator nor an interface wrapper.
    public Enumerator Incoming(int target) => new(_entries, _targets[target].First);

    private void Grow()
    {
        if (_count == int.MaxValue) throw new OutOfMemoryException();
        var capacity = _entries.Length == 0 ? 4 :
            _entries.Length <= int.MaxValue / 2 ? _entries.Length * 2 : int.MaxValue;
        Array.Resize(ref _entries, capacity);
    }

    private struct Endpoints
    {
        public int First;
        public int Last;
    }

    // Reference followed by two Int32 fields avoids a padding gap on 64-bit CLR.
    internal struct Entry
    {
        public readonly BoundResourceExpression Edge;
        public readonly int Parent;
        public int Next;
        public Entry(int parent, BoundResourceExpression edge) { Edge = edge; Parent = parent; Next = 0; }
    }

    internal struct Enumerator
    {
        private readonly Entry[] _entries;
        private int _next;
        private int _current;
        public Enumerator(Entry[] entries, int first) { _entries = entries; _next = first; _current = 0; }
        public Enumerator GetEnumerator() => this;
        public Entry Current => _entries[_current - 1];
        public bool MoveNext()
        {
            if (_next == 0) return false;
            _current = _next;
            _next = _entries[_current - 1].Next;
            return true;
        }
    }
}
