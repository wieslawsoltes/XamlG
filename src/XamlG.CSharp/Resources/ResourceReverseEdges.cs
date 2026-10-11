using System.Runtime.CompilerServices;
using XamlG.Compiler.Resources;

namespace XamlG.CSharp.Resources;

/// <summary>Build-local ordered reverse adjacency. Bounded edge blocks replace
/// per-target List objects without copying edges when the graph grows. Append
/// order is preserved: it determines which competing failure is reported.</summary>
internal sealed class ResourceReverseEdges
{
    private const int FirstCapacity = 4;
    private const int BlockShift = 5;
    private const int BlockCapacity = 1 << BlockShift;
    private readonly int _documentCount;
    private Endpoints[]? _targets;
    private int _firstTarget = -1;
    private int _linearPrefix;
    private Entry[] _first = Array.Empty<Entry>();
    private Entry[][] _blocks = Array.Empty<Entry[]>();
    private int _count;

    public ResourceReverseEdges(int documentCount)
    {
        if (documentCount < 0) throw new ArgumentOutOfRangeException(nameof(documentCount));
        _documentCount = documentCount;
    }

    public void Add(int target, int parent, BoundResourceExpression edge)
    {
        CheckTarget(target);
        // Reserve before publishing either the entry or target endpoints.
        Reserve();
        var link = _count + 1;
        At(link) = new(parent, edge);
        if (_targets == null)
        {
            if (_count == 0) _firstTarget = target;
            else if (target != _firstTarget)
            {
                // Until the first different destination, links are implicit and
                // no per-document endpoint array is needed. Promotion is O(D),
                // not O(E): old edges stay in place and need no relinking pass.
                var targets = new Endpoints[_documentCount];
                targets[_firstTarget] = new Endpoints { First = 1, Last = _count };
                _linearPrefix = _count;
                _targets = targets;
            }
        }
        if (_targets != null)
        {
            ref var endpoints = ref _targets[target];
            if (endpoints.Last == 0) endpoints.First = link;
            else At(endpoints.Last).Next = link;
            endpoints.Last = link;
        }
        _count = link;
    }

    // Used only after construction. A value cursor retains this build-local
    // owner, not a snapshot/copy of its edge buffers or mutable project state.
    public Enumerator Incoming(int target)
    {
        CheckTarget(target);
        return _targets == null
            ? new(this, target == _firstTarget ? 1 : 0, _count)
            : new(this, _targets[target].First, target == _firstTarget ? _linearPrefix : 0);
    }

    private void CheckTarget(int target)
    {
        if (unchecked((uint)target) >= (uint)_documentCount) throw new IndexOutOfRangeException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Entry At(int link)
    {
        var index = link - 1;
        if (index < FirstCapacity) return ref _first[index];
        index -= FirstCapacity;
        return ref _blocks[index >> BlockShift][index & (BlockCapacity - 1)];
    }

    private void Reserve()
    {
        if (_count == int.MaxValue) throw new OutOfMemoryException();
        if (_count < FirstCapacity)
        {
            if (_first.Length == 0) _first = new Entry[FirstCapacity];
            return;
        }
        var index = _count - FirstCapacity;
        if ((index & (BlockCapacity - 1)) != 0) return;
        var block = index >> BlockShift;
        if (block == _blocks.Length)
        {
            // Only the small directory of block references grows. Existing edge
            // records never move/copy; spare edge capacity is at most 31 slots.
            var capacity = _blocks.Length == 0 ? 4 :
                _blocks.Length <= int.MaxValue / 2 ? _blocks.Length * 2 : int.MaxValue;
            Array.Resize(ref _blocks, capacity);
        }
        _blocks[block] = new Entry[BlockCapacity];
    }

    private struct Endpoints
    {
        public int First;
        public int Last;
    }

    internal struct Entry
    {
        public readonly BoundResourceExpression Edge;
        public readonly int Parent;
        public int Next;
        public Entry(int parent, BoundResourceExpression edge) { Edge = edge; Parent = parent; Next = 0; }
    }

    internal struct Enumerator
    {
        private readonly ResourceReverseEdges _owner;
        private readonly int _linearEnd;
        private int _next;
        private Entry _current;
        public Enumerator(ResourceReverseEdges owner, int first, int linearEnd)
        { _owner = owner; _next = first; _current = default; _linearEnd = linearEnd; }
        public Enumerator GetEnumerator() => this;
        public Entry Current => _current;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (_next == 0) return false;
            var link = _next;
            // Load the complete entry once. Current and successor selection share
            // that value instead of repeating block/directory/index checks.
            _current = _owner.At(link);
            _next = link < _linearEnd ? link + 1 : _current.Next;
            return true;
        }
    }
}
