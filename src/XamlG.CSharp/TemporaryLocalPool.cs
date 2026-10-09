using XamlG.Compiler;
using System.Runtime.CompilerServices;

namespace XamlG.CSharp;

/// <summary>Reuses typed locals after an assignment has consumed its temporaries.</summary>
internal sealed class TemporaryLocalPool(EmissionContext context)
{
    private readonly Dictionary<(int Scope, string Type), Stack<string>> _available = new();
    private readonly List<((int Scope, string Type) Key, string Name)> _active = new();
    [Flags]
    private enum Hazard { None = 0, Capture = 1, Raw = 2 }
    private sealed class Identity<T> : IEqualityComparer<T> where T : class
    {
        public static readonly Identity<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
    private readonly Dictionary<BoundObject, Hazard> _objects = new(Identity<BoundObject>.Instance);
    private readonly Dictionary<BoundAssignment, Hazard> _assignments = new(Identity<BoundAssignment>.Instance);
    private Hazard? _document;
    private int _depth;

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
        if (Descriptor(assignment) is { } descriptor) result |= Analyze(descriptor);
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

    private Hazard Analyze(BoundExpression value)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        if (value is BoundObjectExpression obj) return Analyze(obj.Object);
        if (value is BoundMarkupExpression markup) return Analyze(markup.Extension);
        var result = value switch
        {
            BoundRawExpression => Hazard.Raw,
            BoundReferenceExpression or BoundLambdaExpression { IsStatic: false } or
                BoundDeferredExpression { UsesFunctionPointer: false } => Hazard.Capture,
            BoundChoiceExpression choice => Analyze(choice.Extension),
            _ => Hazard.None
        };
        foreach (var child in BoundTraversal.Children(value, includeDeferred: true)) result |= Analyze(child);
        return result;
    }
}
