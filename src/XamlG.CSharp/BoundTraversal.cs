using System.Collections.Immutable;
using XamlG.Compiler;

namespace XamlG.CSharp;

internal static class BoundTraversal
{
    public static IEnumerable<BoundObject> Objects(BoundObject root, bool includeDeferred = false)
    {
        yield return root;
        // Forwarding each result through recursive iterators costs O(nodes * depth).
        // Keep one iterator and push existing expressions in reverse visit order;
        // no child arrays or LINQ iterators are needed for this whole-tree walk.
        var pending = new Stack<BoundExpression>();
        PushExpressions(pending, root);
        while (pending.Count != 0)
        {
            var expression = pending.Pop();
            switch (expression)
            {
                case BoundObjectExpression obj:
                    yield return obj.Object;
                    PushExpressions(pending, obj.Object);
                    break;
                case BoundMarkupExpression markup:
                    yield return markup.Extension;
                    PushExpressions(pending, markup.Extension);
                    break;
                default:
                    if (expression is BoundChoiceExpression choice) yield return choice.Extension;
                    PushChildren(pending, expression, includeDeferred);
                    break;
            }
        }
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
    private static void Push(Stack<BoundExpression> pending, ImmutableArray<BoundExpression> values)
    {
        for (var i = values.Length - 1; i >= 0; i--) pending.Push(values[i]);
    }

    private static void PushExpressions(Stack<BoundExpression> pending, BoundObject value)
    {
        for (var i = value.Assignments.Length - 1; i >= 0; i--)
            switch (value.Assignments[i])
            {
                case BoundSetAssignment set: pending.Push(set.Value); break;
                case BoundAdaptedSetAssignment adapted: pending.Push(adapted.Value); break;
                case BoundDynamicSetAssignment dynamic: pending.Push(dynamic.Value); break;
                case BoundEventAssignment { Value: { } handler }: pending.Push(handler); break;
                case BoundAddAssignment add:
                    if (add.PostCall is { } postAdd) Push(pending, postAdd.Arguments);
                    Push(pending, add.Arguments);
                    break;
                case BoundCallAssignment call:
                    if (call.PostCall is { } postCall) Push(pending, postCall.Arguments);
                    Push(pending, call.Arguments);
                    if (call.TargetDescriptor is { } descriptor) pending.Push(descriptor);
                    break;
            }
        Push(pending, value.Arguments);
    }

    private static void PushCalls(Stack<BoundExpression> pending, ImmutableArray<BoundBuilderCall> calls)
    {
        for (var i = calls.Length - 1; i >= 0; i--) Push(pending, calls[i].Arguments);
    }

    private static void PushChildren(Stack<BoundExpression> pending, BoundExpression expression, bool includeDeferred)
    {
        switch (expression)
        {
            case BoundChoiceExpression choice:
                if (choice.Default is { } fallback) pending.Push(fallback);
                for (var i = choice.Branches.Length - 1; i >= 0; i--)
                {
                    pending.Push(choice.Branches[i].Value);
                    pending.Push(choice.Branches[i].Option);
                }
                PushExpressions(pending, choice.Extension);
                break;
            case BoundCastExpression cast: pending.Push(cast.Value); break;
            case BoundArrayExpression array: Push(pending, array.Values); break;
            case BoundNewExpression creation:
                for (var i = creation.Initializers.Length - 1; i >= 0; i--) pending.Push(creation.Initializers[i].Value);
                Push(pending, creation.Arguments);
                break;
            case BoundBuilderExpression builder:
                PushCalls(pending, builder.Calls);
                pending.Push(builder.Creation);
                break;
            case BoundScopedInitializationExpression scoped:
                PushCalls(pending, scoped.Calls);
                pending.Push(scoped.Creation);
                break;
            case BoundCollectionExpression collection: Push(pending, collection.Values); break;
            case BoundCachedExpression cached: pending.Push(cached.Value); break;
            case BoundValueConverterExpression converter: pending.Push(converter.Value); break;
            case BoundCallExpression call:
                Push(pending, call.Arguments);
                if (call.Receiver is { } receiver) pending.Push(receiver);
                break;
            case BoundLambdaExpression lambda: pending.Push(lambda.Body); break;
            case BoundPropertyAccessExpression property:
                Push(pending, property.IndexArguments);
                pending.Push(property.Receiver);
                break;
            case BoundFieldAccessExpression field: pending.Push(field.Receiver); break;
            case BoundAssignmentExpression assignment:
                pending.Push(assignment.Value);
                pending.Push(assignment.Target);
                break;
            case BoundMethodGroupExpression { Receiver: { } methodReceiver }: pending.Push(methodReceiver); break;
            case BoundDeferredExpression deferred when includeDeferred: pending.Push(deferred.Content); break;
        }
    }
    public static IEnumerable<BoundExpression> Children(BoundExpression expression, bool includeDeferred) => expression switch
    {
        BoundChoiceExpression c => Expressions(c.Extension).Concat(c.Branches.SelectMany(b => new[] { b.Option, b.Value })).Concat(c.Default == null ? Array.Empty<BoundExpression>() : new[] { c.Default }),
        BoundCastExpression c => new[] { c.Value }, BoundArrayExpression a => a.Values,
        BoundNewExpression n => n.Arguments.Concat(n.Initializers.Select(initializer => initializer.Value)),
        BoundBuilderExpression b => new[] { b.Creation }.Concat(b.Calls.SelectMany(call => call.Arguments)),
        BoundScopedInitializationExpression s => new[] { s.Creation }.Concat(s.Calls.SelectMany(call => call.Arguments)),
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
