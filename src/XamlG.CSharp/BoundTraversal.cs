using XamlG.Compiler;

namespace XamlG.CSharp;

internal static class BoundTraversal
{
    public static IEnumerable<BoundObject> Objects(BoundObject root, bool includeDeferred = false)
    {
        yield return root;
        foreach (var expression in Expressions(root)) foreach (var child in Objects(expression, includeDeferred)) yield return child;
    }
    public static IEnumerable<BoundExpression> Expressions(BoundObject root)
    {
        foreach (var argument in root.Arguments) yield return argument;
        foreach (var assignment in root.Assignments) foreach (var expression in Expressions(assignment)) yield return expression;
    }
    public static IEnumerable<BoundExpression> Expressions(BoundAssignment assignment) => assignment switch
    {
        BoundSetAssignment s => new[] { s.Value }, BoundAddAssignment a => a.Arguments.Concat(a.PostCall?.Arguments ?? []),
        BoundEventAssignment { Value: { } value } => new[] { value },
        BoundDynamicSetAssignment d => new[] { d.Value }, BoundAdaptedSetAssignment a => new[] { a.Value },
        BoundCallAssignment c => (c.TargetDescriptor == null ? c.Arguments : c.Arguments.Insert(0, c.TargetDescriptor)).Concat(c.PostCall?.Arguments ?? []), _ => Array.Empty<BoundExpression>()
    };
    private static IEnumerable<BoundObject> Objects(BoundExpression expression, bool includeDeferred)
    {
        if (expression is BoundChoiceExpression choice) { yield return choice.Extension; }
        if (expression is BoundObjectExpression obj) { foreach (var child in Objects(obj.Object, includeDeferred)) yield return child; yield break; }
        if (expression is BoundMarkupExpression markup) { foreach (var child in Objects(markup.Extension, includeDeferred)) yield return child; yield break; }
        foreach (var child in Children(expression, includeDeferred)) foreach (var nested in Objects(child, includeDeferred)) yield return nested;
    }
    public static bool ContainsReference(BoundExpression expression) => expression is BoundReferenceExpression || Children(expression, false).Any(ContainsReference);
    public static IEnumerable<BoundExpression> Children(BoundExpression expression, bool includeDeferred) => expression switch
    {
        BoundChoiceExpression c => Expressions(c.Extension).Concat(c.Branches.SelectMany(b => new[] { b.Option, b.Value })).Concat(c.Default == null ? Array.Empty<BoundExpression>() : new[] { c.Default }),
        BoundCastExpression c => new[] { c.Value }, BoundArrayExpression a => a.Values, BoundNewExpression n => n.Arguments,
        BoundCollectionExpression c => c.Values,
        BoundCachedExpression c => new[] { c.Value },
        BoundValueConverterExpression c => new[] { c.Value },
        BoundCallExpression c => c.Receiver == null ? c.Arguments : c.Arguments.Insert(0, c.Receiver),
        BoundLambdaExpression l => new[] { l.Body },
        BoundPropertyAccessExpression p => p.IndexArguments.Insert(0, p.Receiver),
        BoundFieldAccessExpression f => new[] { f.Receiver },
        BoundAssignmentExpression a => new[] { a.Target, a.Value },
        BoundMethodGroupExpression m when m.Receiver != null => new[] { m.Receiver },
        BoundDeferredExpression d when includeDeferred => new[] { d.Content },
        BoundObjectExpression o => Expressions(o.Object), BoundMarkupExpression m => Expressions(m.Extension),
        _ => Array.Empty<BoundExpression>()
    };
}
