using Avalonia.Controls;
using Avalonia.Styling;

namespace XamlG.IntelligentUI.Avalonia;

internal static class UiAvaloniaSelectors
{
    internal static Selector Build(Selector? previous, UiStyleSelector selector, Func<string, Type> resolve, bool nesting)
    {
        Selector? current = previous;
        foreach (var step in selector.Steps)
        {
            current = step.Relation switch
            {
                UiSelectorRelation.Root => nesting ? current.Nesting() : current,
                UiSelectorRelation.Child => current!.Child(),
                UiSelectorRelation.Descendant => current.Descendant(),
                UiSelectorRelation.Template => current!.Template(),
                _ => throw new UiException("invalid_style", "Invalid selector relationship.")
            };
            current = Compound(current, step.Predicate, resolve, nesting && step.Relation == UiSelectorRelation.Root);
        }
        return current ?? throw new UiException("invalid_style", "Empty native selector.");
    }
    private static Selector Compound(Selector? current, UiSelectorCompound predicate, Func<string, Type> resolve, bool skipType = false)
    {
        if (!skipType && predicate.Type is { } typeName)
            current = predicate.IncludeDerived ? current.Is(resolve(typeName)) : current.OfType(resolve(typeName));
        if (predicate.Name != null) current = current.Name(predicate.Name);
        foreach (var name in predicate.Classes) current = current.Class(name);
        foreach (var name in predicate.PseudoClasses) current = current.Class(":" + name);
        foreach (var negative in predicate.Negations) current = current.Not(Compound(null, negative, resolve));
        foreach (var position in predicate.Positions)
            current = position.FromEnd ? current.NthLastChild(position.Step, position.Offset) : current.NthChild(position.Step, position.Offset);
        return current ?? ((Selector?)null).Is<Control>();
    }
}
