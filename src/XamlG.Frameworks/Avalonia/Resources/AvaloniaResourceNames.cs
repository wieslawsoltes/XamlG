using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Resources;

internal static class AvaloniaResourceNames
{
    public static bool Contains(BoundExpression expression) => expression switch
    {
        BoundObjectExpression value => Contains(value.Object),
        BoundMarkupExpression value => Contains(value.Extension),
        BoundCastExpression value => Contains(value.Value),
        BoundCachedExpression value => Contains(value.Value),
        BoundValueConverterExpression value => Contains(value.Value),
        BoundArrayExpression value => value.Values.Any(Contains),
        BoundCollectionExpression value => value.Values.Any(Contains),
        BoundNewExpression value => value.Arguments.Any(Contains),
        BoundCallExpression value => value.Receiver != null && Contains(value.Receiver) || value.Arguments.Any(Contains),
        BoundChoiceExpression value => Contains(value.Extension) || value.Branches.Any(branch => Contains(branch.Option) || Contains(branch.Value)) || value.Default != null && Contains(value.Default),
        // Deferred content starts a separate scope whose names do not force eager resources.
        _ => false
    };

    private static bool Contains(BoundObject value) => value.Name != null || value.Arguments.Any(Contains) || value.Assignments.Any(assignment => assignment switch
    {
        BoundSetAssignment set => Contains(set.Value),
        BoundAddAssignment add => add.Arguments.Any(Contains),
        BoundEventAssignment { Value: { } handler } => Contains(handler),
        BoundAdaptedSetAssignment set => Contains(set.Value),
        BoundDynamicSetAssignment set => Contains(set.Value),
        BoundCallAssignment call => call.Arguments.Any(Contains),
        _ => false
    });
}
