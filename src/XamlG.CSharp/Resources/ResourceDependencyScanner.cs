using System.Collections.Immutable;
using System.Threading;
using XamlG.Compiler;
using XamlG.Compiler.Resources;

namespace XamlG.CSharp.Resources;

/// <summary>Collects the first reference for each ordinal URI in the historical
/// reverse depth-first order. This is dependency discovery, NOT an execution walk.</summary>
internal sealed class ResourceDependencyScanner
{
    private readonly CancellationToken _cancellation;
    // Each outgoing edge of an admitted aggregate may expand at most 31 small
    // cast/binary nodes before another exact-admission boundary. Binary branches
    // divide this work budget; it is NOT a 31-level branching depth allowance.
    private const int SegmentBudget = 31;
    private Stack<Pending>? _pending;
    private object? _firstNode;
    private ResourceVisitFilter _visits;
    private BoundResourceExpression? _firstResource;
    private HashSet<string>? _uris;
    private ImmutableArray<BoundResourceExpression>.Builder? _resources;

    private ResourceDependencyScanner(CancellationToken cancellation) => _cancellation = cancellation;

    public static ImmutableArray<BoundResourceExpression> Scan(BoundObject root, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (root.Arguments.IsEmpty && root.Assignments.IsEmpty)
            return ImmutableArray<BoundResourceExpression>.Empty;
        return new ResourceDependencyScanner(cancellation).Run(root);
    }

    private ImmutableArray<BoundResourceExpression> Run(BoundObject root)
    {
        // The common graph-only/direct-include shape needs no traversal stack or
        // identity admission. The single-level preflight does not inspect other kinds
        // of assignments or containers; those retain the general child grammar.
        if (TryScanFlat(root)) return Finish();
        _firstNode = root;
        var pending = _pending = new Stack<Pending>();
        PushObject(root);
        while (pending.Count != 0)
        {
            _cancellation.ThrowIfCancellationRequested();
            var item = pending.Pop();
            switch (item.Node)
            {
                case BoundResourceExpression resource:
                    AddResource(resource);
                    break;
                case BoundCastExpression cast:
                    if (item.Budget != 0 || CanExpand(cast))
                        Push(cast.Value, item.Budget == 0 ? SegmentBudget : item.Budget - 1);
                    break;
                case BoundArrayExpression array:
                    PushArray(array, item.Budget);
                    break;
                case BoundObject owner:
                    // Distinct expression wrappers can share the SAME BoundObject.
                    // Marking wrappers alone would still repeatedly expand its edges.
                    if (CanExpand(owner)) PushObject(owner);
                    break;
                case BoundObjectExpression wrapper:
                    pending.Push(new(wrapper.Object, 0));
                    break;
                case BoundMarkupExpression markup:
                    _pending!.Push(new(markup.Extension, 0));
                    break;
                case BoundChoiceExpression choice:
                    if (!CanExpand(choice)) break;
                    // Push in forward order; LIFO visits default, branches and then
                    // the extension, exactly like Children(choice, true) previously.
                    _pending!.Push(new(choice.Extension, 0));
                    foreach (var branch in choice.Branches)
                    {
                        _cancellation.ThrowIfCancellationRequested();
                        Push(branch.Option, SegmentBudget);
                        Push(branch.Value, SegmentBudget);
                    }
                    if (choice.Default is { } fallback) Push(fallback, SegmentBudget);
                    break;
                case BoundExpression expression:
                    PushChildren(expression);
                    break;
            }
        }
        return Finish();
    }

    private ImmutableArray<BoundResourceExpression> Finish()
    {
        _cancellation.ThrowIfCancellationRequested();
        return _resources != null ? _resources.ToImmutable() : _firstResource is { } first
            ? ImmutableArray.Create(first) : ImmutableArray<BoundResourceExpression>.Empty;
    }

    private bool TryScanFlat(BoundObject root)
    {
        foreach (var argument in root.Arguments)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (argument is not (BoundResourceExpression or BoundConstantExpression)) return false;
        }
        foreach (var assignment in root.Assignments)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (assignment is not BoundSetAssignment { Value: BoundResourceExpression or BoundConstantExpression }) return false;
        }
        for (var i = root.Assignments.Length - 1; i >= 0; i--)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (((BoundSetAssignment)root.Assignments[i]).Value is BoundResourceExpression resource) AddResource(resource);
        }
        for (var i = root.Arguments.Length - 1; i >= 0; i--)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (root.Arguments[i] is BoundResourceExpression resource) AddResource(resource);
        }
        return true;
    }

    private void PushObject(BoundObject value)
    {
        foreach (var argument in value.Arguments) Push(argument, SegmentBudget);
        foreach (var assignment in value.Assignments)
        {
            _cancellation.ThrowIfCancellationRequested();
            // Reuse the common assignment-child grammar: descriptors, arguments
            // and post-calls retain their order without intermediate child arrays.
            foreach (var expression in BoundTraversal.Expressions(assignment)) Push(expression, SegmentBudget);
        }
    }

    private void PushArray(BoundArrayExpression array, int budget)
    {
        if (array.Values.IsEmpty) return;
        var admit = budget == 0 || array.Values.Length > 2;
        if (admit && !CanExpand(array)) return;
        var next = admit ? SegmentBudget : array.Values.Length == 2 ? (budget - 1) / 2 : budget - 1;
        foreach (var value in array.Values) Push(value, next);
    }

    private void PushChildren(BoundExpression expression)
    {
        // Leaves never enter the identity set. Probe only the first child before
        // checking identity; a repeated composite is NOT traversed to completion.
        // Reuse the existing grammar for shapes other than casts, arrays, objects and choices.
        var children = BoundTraversal.Children(expression, includeDeferred: true).GetEnumerator();
        try
        {
            if (!children.MoveNext() || !CanExpand(expression)) return;
            do { Push(children.Current, SegmentBudget); } while (children.MoveNext());
        }
        finally { children.Dispose(); }
    }

    private void Push(BoundExpression expression, int budget)
    {
        _cancellation.ThrowIfCancellationRequested();
        _pending!.Push(new(expression, budget));
    }

    private readonly struct Pending(object node, int budget)
    {
        public object Node { get; } = node;
        public int Budget { get; } = budget;
    }

    private bool CanExpand(object value)
    {
        if (ReferenceEquals(_firstNode, value)) return false;
        // Flat documents keep their sole root identity inline and never allocate
        // a visit filter. Composite trees use delayed exact memoization: at most
        // two expansions per identity, including saturated/colliding fingerprints.
        return _visits.TryExpand(value);
    }

    private void AddResource(BoundResourceExpression resource)
    {
        if (_firstResource == null) { _firstResource = resource; return; }
        if (_uris == null)
        {
            if (string.Equals(_firstResource.Resource.Uri, resource.Resource.Uri, StringComparison.Ordinal)) return;
            _uris = new(StringComparer.Ordinal) { _firstResource.Resource.Uri, resource.Resource.Uri };
            _resources = ImmutableArray.CreateBuilder<BoundResourceExpression>(4);
            _resources.Add(_firstResource);
        }
        else if (!_uris.Add(resource.Resource.Uri)) return;
        _resources!.Add(resource);
    }
}
