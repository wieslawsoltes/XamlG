using XamlG.Compiler;

namespace XamlG.Tooling;

/// <summary>Iterative traversal of the typed graph, including deferred template scopes.</summary>
public static class BoundDocumentTraversal
{
    public static IEnumerable<BoundObject> Objects(BoundDocument document)
    {
        if (document.Root == null) yield break;
        yield return document.Root;
        foreach (var expression in Expressions(document))
            if (expression is BoundObjectExpression obj) yield return obj.Object;
            else if (expression is BoundMarkupExpression markup) yield return markup.Extension;
            else if (expression is BoundChoiceExpression choice) yield return choice.Extension;
    }

    public static IEnumerable<BoundExpression> Expressions(BoundDocument document)
    {
        if (document.Root == null) yield break;
        var pending = new Stack<BoundExpression>(ObjectExpressions(document.Root).Reverse());
        while (pending.Count != 0)
        {
            var value = pending.Pop();
            yield return value;
            foreach (var child in Children(value).Reverse()) pending.Push(child);
        }
    }

    private static IEnumerable<BoundExpression> ObjectExpressions(BoundObject value)
    {
        foreach (var argument in value.Arguments) yield return argument;
        foreach (var assignment in value.Assignments)
        {
            var values = assignment switch
            {
                BoundSetAssignment set => new[] { set.Value },
                BoundAddAssignment add => add.Arguments.Concat(add.PostCall?.Arguments ?? []),
                BoundEventAssignment { Value: { } handler } => new[] { handler },
                BoundAdaptedSetAssignment adapted => new[] { adapted.Value },
                BoundDynamicSetAssignment dynamicSet => new[] { dynamicSet.Value },
                BoundCallAssignment call => (call.TargetDescriptor == null ? call.Arguments.AsEnumerable() : call.Arguments.Prepend(call.TargetDescriptor)).Concat(call.PostCall?.Arguments ?? []),
                _ => Enumerable.Empty<BoundExpression>()
            };
            foreach (var expression in values) yield return expression;
        }
    }

    private static IEnumerable<BoundExpression> Children(BoundExpression expression) => expression switch
    {
        BoundObjectExpression obj => ObjectExpressions(obj.Object),
        BoundMarkupExpression markup => ObjectExpressions(markup.Extension),
        BoundChoiceExpression choice => ObjectExpressions(choice.Extension).Concat(choice.Branches.SelectMany(b => new[] { b.Option, b.Value })).Concat(choice.Default == null ? Enumerable.Empty<BoundExpression>() : new[] { choice.Default }),
        BoundCastExpression cast => new[] { cast.Value },
        BoundCachedExpression cached => new[] { cached.Value },
        BoundValueConverterExpression converter => new[] { converter.Value },
        BoundArrayExpression array => array.Values,
        BoundCollectionExpression collection => collection.Values,
        BoundNewExpression creation => creation.Arguments.Concat(creation.Initializers.Select(initializer => initializer.Value)),
        BoundCallExpression call => call.Receiver == null ? call.Arguments.AsEnumerable() : call.Arguments.Prepend(call.Receiver),
        BoundDeferredExpression deferred => new[] { deferred.Content },
        BoundLambdaExpression lambda => new[] { lambda.Body },
        BoundPropertyAccessExpression property => property.IndexArguments.Prepend(property.Receiver),
        BoundFieldAccessExpression field => new[] { field.Receiver },
        BoundAssignmentExpression assignment => new[] { assignment.Target, assignment.Value },
        BoundMethodGroupExpression { Receiver: { } receiver } => new[] { receiver },
        _ => Enumerable.Empty<BoundExpression>()
    };
}
