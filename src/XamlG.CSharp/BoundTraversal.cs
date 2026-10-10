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
    public static BoundExpressionSequence Expressions(BoundAssignment assignment) => assignment switch
    {
        BoundSetAssignment s => new(s.Value, [], [], null, null),
        BoundAddAssignment a => new(null, a.Arguments, a.PostCall?.Arguments ?? [], null, null),
        BoundEventAssignment { Value: { } value } => new(value, [], [], null, null),
        BoundDynamicSetAssignment d => new(d.Value, [], [], null, null),
        BoundAdaptedSetAssignment a => new(a.Value, [], [], null, null),
        BoundCallAssignment c => new(c.TargetDescriptor, c.Arguments, c.PostCall?.Arguments ?? [], null, null),
        _ => default
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
    public static BoundExpressionSequence Children(BoundExpression expression, bool includeDeferred) => expression switch
    {
        BoundChoiceExpression c => new(null, [], [], null, ChoiceChildren(c)),
        BoundCastExpression c => new(c.Value, [], [], null, null),
        BoundArrayExpression a => new(null, a.Values, [], null, null),
        BoundNewExpression n => new(null, n.Arguments, [], null, n.Initializers.IsEmpty ? null : InitializerValues(n.Initializers)),
        BoundBuilderExpression b => new(b.Creation, [], [], null, b.Calls.IsEmpty ? null : CallArguments(b.Calls)),
        BoundScopedInitializationExpression s => new(s.Creation, [], [], null, s.Calls.IsEmpty ? null : CallArguments(s.Calls)),
        BoundCollectionExpression c => new(null, c.Values, [], null, null),
        BoundCachedExpression c => new(c.Value, [], [], null, null),
        BoundValueConverterExpression c => new(c.Value, [], [], null, null),
        BoundCallExpression c => new(c.Receiver, c.Arguments, [], null, null),
        BoundLambdaExpression l => new(l.Body, [], [], null, null),
        BoundPropertyAccessExpression p => new(p.Receiver, p.IndexArguments, [], null, null),
        BoundFieldAccessExpression f => new(f.Receiver, [], [], null, null),
        BoundAssignmentExpression a => new(a.Target, [], [], a.Value, null),
        BoundMethodGroupExpression m when m.Receiver != null => new(m.Receiver, [], [], null, null),
        BoundDeferredExpression d when includeDeferred => new(d.Content, [], [], null, null),
        BoundObjectExpression o => new(null, [], [], null, Expressions(o.Object)),
        BoundMarkupExpression m => new(null, [], [], null, Expressions(m.Extension)),
        _ => default
    };

    private static IEnumerable<BoundExpression> InitializerValues(ImmutableArray<BoundPropertyInitialization> initializers)
    {
        foreach (var initializer in initializers) yield return initializer.Value;
    }
    private static IEnumerable<BoundExpression> CallArguments(ImmutableArray<BoundBuilderCall> calls)
    {
        foreach (var call in calls) foreach (var argument in call.Arguments) yield return argument;
    }
    private static IEnumerable<BoundExpression> ChoiceChildren(BoundChoiceExpression choice)
    {
        foreach (var expression in Expressions(choice.Extension)) yield return expression;
        foreach (var branch in choice.Branches) { yield return branch.Option; yield return branch.Value; }
        if (choice.Default is { } fallback) yield return fallback;
    }
}
