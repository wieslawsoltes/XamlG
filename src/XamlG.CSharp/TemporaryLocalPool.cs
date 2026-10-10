using XamlG.Compiler;
using System.Runtime.CompilerServices;

namespace XamlG.CSharp;

/// <summary>Reuses typed locals after an assignment has consumed its temporaries.</summary>
internal sealed class TemporaryLocalPool(EmissionContext context)
{
    private readonly Dictionary<(int Scope, string Type), Stack<string>> _available = new();
    private readonly List<((int Scope, string Type) Key, string Name)> _active = new();
    [Flags]
    private enum Hazard { None = 0, Capture = 1, Raw = 2, Reference = 4 }
    private sealed class Identity<T> : IEqualityComparer<T> where T : class
    {
        public static readonly Identity<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
    private readonly Dictionary<BoundObject, Hazard> _objects = new(Identity<BoundObject>.Instance);
    private readonly Dictionary<BoundAssignment, Hazard> _assignments = new(Identity<BoundAssignment>.Instance);
    // Completed composite results belong to this emission only. Reference identity
    // is essential: structural hashing would recursively expand the graph again.
    private Dictionary<BoundExpression, Hazard>? _expressions;
    private Hazard? _document;
    private int _depth;

    public bool ContainsReference(BoundAssignment assignment) => (Analyze(assignment) & Hazard.Reference) != 0;

    public Lease EnterAssignment(BoundObject owner, BoundAssignment assignment)
    {
        _document ??= Analyze(context.Document.Root!);
        // Raw code can refer to any generated local, including a previous sibling's.
        // Other captures protect the containing assignments, not unrelated siblings.
        if ((_document.Value & Hazard.Raw) != 0 || Analyze(assignment) != Hazard.None ||
            owner.Type.IsValueType && assignment is BoundSetAssignment) return default;
        _depth++;
        return new(this, _active.Count);
    }

    public string Declare(string type, string expression, string role, bool inferred = false)
    {
        var (name, reused) = Reserve(type, role);
        context.Writer.Line((reused ? string.Empty : (inferred ? "var" : type) + " ") + name + " = " + expression + ";");
        return name;
    }

    public string OutArgument(string type, string role, out string name)
    {
        var reserved = Reserve(type, role);
        name = reserved.Name;
        return "out " + (reserved.Reused ? string.Empty : "var ") + name;
    }

    private (string Name, bool Reused) Reserve(string type, string role)
    {
        // A branch-local declaration cannot be reused outside that branch, or by
        // another generated function, even when its assignment lifetime has ended.
        var key = (context.Writer.ScopeId, type);
        var reused = _depth != 0 && _available.TryGetValue(key, out var names) && names.Count != 0;
        var name = reused ? _available[key].Pop() : context.Temporary(role);
        if (_depth != 0) _active.Add((key, name));
        return (name, reused);
    }

    private void Release(int first)
    {
        for (var index = _active.Count - 1; index >= first; index--)
        {
            var local = _active[index];
            if (!_available.TryGetValue(local.Key, out var names)) _available.Add(local.Key, names = new());
            names.Push(local.Name);
        }
        _active.RemoveRange(first, _active.Count - first);
        _depth--;
    }

    public readonly struct Lease(TemporaryLocalPool pool, int first) : IDisposable
    {
        public void Dispose() => pool?.Release(first);
    }

    // Retained providers hold frame objects, which may safely come from reused
    // local slots. Closures instead retain the variable itself. Propagate their
    // hazard to every containing assignment so its target/frame cannot enter the
    // pool. Memoize by identity: bound records can share subtrees, and structural
    // equality would repeatedly walk those subtrees. Never stop at a capture: raw
    // code elsewhere in the document still requires the global fallback.
    private Hazard Analyze(BoundObject value)
    {
        if (_objects.TryGetValue(value, out var cached)) return cached;
        context.Cancellation.ThrowIfCancellationRequested();
        var result = Hazard.None;
        foreach (var argument in value.Arguments) result |= Analyze(argument);
        foreach (var assignment in value.Assignments)
        {
            result |= Analyze(assignment);
            if (value.Type.IsValueType && assignment is BoundSetAssignment) result |= Hazard.Capture;
        }
        _objects.Add(value, result);
        return result;
    }

    private Hazard Analyze(BoundAssignment assignment)
    {
        if (_assignments.TryGetValue(assignment, out var cached)) return cached;
        context.Cancellation.ThrowIfCancellationRequested();
        var result = assignment switch
        {
            BoundRawAssignment => Hazard.Raw,
            BoundEventAssignment => Hazard.Capture,
            _ => Hazard.None
        };
        // Descriptors contribute lifetime hazards, but only assignment values
        // (and explicit call arguments/descriptors) schedule name fixups.
        if (Descriptor(assignment) is { } descriptor) result |= Analyze(descriptor) & ~Hazard.Reference;
        foreach (var expression in BoundTraversal.Expressions(assignment)) result |= Analyze(expression);
        _assignments.Add(assignment, result);
        return result;
    }

    private static BoundExpression? Descriptor(BoundAssignment assignment) => assignment switch
    {
        BoundSetAssignment set => set.Member.TargetDescriptor,
        BoundAdaptedSetAssignment set => set.Member.TargetDescriptor,
        BoundDynamicSetAssignment set => set.Target.TargetDescriptor,
        BoundAddAssignment add => add.Collection?.TargetDescriptor,
        BoundEventAssignment ev => ev.Event.TargetDescriptor,
        _ => null
    };

    private Hazard Analyze(BoundExpression value) => AnalyzeExpression(value, 0);

    private Hazard AnalyzeExpression(BoundExpression value, int remaining)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        if (value is BoundObjectExpression obj) return Analyze(obj.Object);
        if (value is BoundMarkupExpression markup) return Analyze(markup.Extension);
        // The overwhelmingly common leaves need neither edge enumeration nor a
        // dictionary lookup, including when a prior composite populated the cache.
        if (value is BoundConstantExpression) return Hazard.None;
        if (value is BoundRawExpression) return Hazard.Raw;
        if (value is BoundReferenceExpression) return Hazard.Capture | Hazard.Reference;
        // Unary casts and binary arrays can traverse a bounded segment without
        // retaining every intermediate node. Cache every fourth level, and every
        // other shape immediately. Each outgoing edge expands at most 7 skipped
        // nodes before 8 cache boundaries. Repeated DAG work therefore stays
        // bounded even at high-fanout parents. No completed result is evicted.
        var memoize = remaining == 0 || value is not (BoundCastExpression or BoundArrayExpression { Values.Length: <= 2 });
        if (memoize && _expressions != null && _expressions.TryGetValue(value, out var cached)) return cached;
        var next = memoize ? 3 : remaining - 1;
        var result = value switch
        {
            BoundLambdaExpression { IsStatic: false } or
                BoundDeferredExpression { UsesFunctionPointer: false } => Hazard.Capture,
            BoundChoiceExpression choice => Analyze(choice.Extension),
            _ => Hazard.None
        };
        var children = BoundTraversal.Children(value, includeDeferred: true).GetEnumerator();
        try
        {
            if (!children.MoveNext()) return result;
            do { result |= AnalyzeExpression(children.Current, next); } while (children.MoveNext());
        }
        finally { children.Dispose(); }
        // Mask the wrapper, never the cached child: the same child can also occur
        // outside a deferred factory and require an outer name fixup there.
        if (value is BoundDeferredExpression) result &= ~Hazard.Reference;
        if (memoize) (_expressions ??= new(Identity<BoundExpression>.Instance)).Add(value, result);
        return result;
    }
}
