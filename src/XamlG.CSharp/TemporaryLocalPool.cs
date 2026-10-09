using XamlG.Compiler;

namespace XamlG.CSharp;

/// <summary>Reuses typed locals after an assignment has consumed its temporaries.</summary>
internal sealed class TemporaryLocalPool(EmissionContext context)
{
    private readonly Dictionary<(int Scope, string Type), Stack<string>> _available = new();
    private readonly List<((int Scope, string Type) Key, string Name)> _active = new();
    private bool? _safe;
    private int _depth;

    public Lease EnterAssignment()
    {
        _safe ??= Safe(context.Document.Root!);
        if (!_safe.Value) return default;
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
    // local slots. Closures instead retain the variable itself. Keep documents
    // containing captures, value-type property closures or trusted raw code out
    // of the pool until those lifetimes can be proven independently.
    private static bool Safe(BoundObject value)
    {
        if (!value.Arguments.All(Safe)) return false;
        foreach (var assignment in value.Assignments)
        {
            if (assignment is BoundEventAssignment or BoundRawAssignment ||
                value.Type.IsValueType && assignment is BoundSetAssignment ||
                Descriptor(assignment) is { } descriptor && !Safe(descriptor) ||
                !BoundTraversal.Expressions(assignment).All(Safe)) return false;
        }
        return true;
    }

    private static BoundExpression? Descriptor(BoundAssignment assignment) => assignment switch
    {
        BoundSetAssignment set => set.Member.TargetDescriptor,
        BoundAdaptedSetAssignment set => set.Member.TargetDescriptor,
        BoundDynamicSetAssignment set => set.Target.TargetDescriptor,
        _ => null
    };

    private static bool Safe(BoundExpression value)
    {
        if (value is BoundReferenceExpression or BoundRawExpression or BoundLambdaExpression { IsStatic: false } or
            BoundDeferredExpression { UsesFunctionPointer: false }) return false;
        if (value is BoundObjectExpression obj) return Safe(obj.Object);
        if (value is BoundMarkupExpression markup) return Safe(markup.Extension);
        if (value is BoundChoiceExpression choice && !Safe(choice.Extension)) return false;
        return BoundTraversal.Children(value, includeDeferred: true).All(Safe);
    }
}
