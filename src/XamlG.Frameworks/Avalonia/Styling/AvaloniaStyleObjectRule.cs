using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed class AvaloniaStyleObjectRule : IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target)
    {
        var inherited = FindTarget(context, target);
        var declared = target.Scope.Directive(target.Syntax, AvaloniaStyleMetadata.SetterTargetType);
        var declaredType = declared == null ? null : AvaloniaBindingScopeRule.ResolveDataType(context, declared.Value, target.Scope, declared.ValueSpan);
        if (AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.Style))
        {
            target.Annotations.Set(AvaloniaStyleAnnotations.AssignedProperties, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
            var source = TextMember(target.Syntax, AvaloniaStyleMetadata.SelectorMember);
            if (source is { } selector && !string.IsNullOrWhiteSpace(selector.Text))
            {
                var syntax = AvaloniaSelectorParser.Parse(selector.Text, selector.Span, context.Diagnostics.Add, context.Cancellation);
                var bound = syntax == null ? null : new AvaloniaSelectorBinder(context, target.Scope, inherited, FindNestingSelector(context, target)).Bind(syntax);
                if (bound != null)
                {
                    target.Annotations.Set(AvaloniaStyleAnnotations.Selector, bound);
                    if (bound.TargetType != null) target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, bound.TargetType);
                }
            }
            else
            {
                var owner = FindStyleOwner(context, target);
                if (owner != null) target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, owner);
                if (source != null && context.Types.Find(AvaloniaStyleMetadata.Selector) is { } selectorType)
                    target.Annotations.Set(AvaloniaStyleAnnotations.Selector, new BoundSelector(new BoundConstantExpression(null, selectorType, source.Value.Span), owner));
            }
        }
        else if (AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.ControlTheme) || AvaloniaStyleScope.IsTemplate(target.Type))
        {
            // Always determine the nearest semantic owner. An outer template's target
            // must not shadow a concrete control receiving a nested template.
            var theme = AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.ControlTheme);
            if (!theme)
                inherited = BindingTargetTypeResolver.FindTemplateOwner(context, target);
            var hasTarget = AvaloniaStyleTargetType.TryRead(context, target, out var type);
            if (!hasTarget)
            {
                if (theme) context.Report("XG3116", "ControlTheme requires an explicit TargetType.", target.Syntax.NameSpan);
                else type = inherited ?? context.Types.Find(AvaloniaMetadata.Control);
            }
            if (type != null) target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, type);
            if (theme)
                target.Annotations.Set(AvaloniaStyleAnnotations.AssignedProperties, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        }
        else if (AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.Setter))
        {
            var source = TextMember(target.Syntax, AvaloniaStyleMetadata.PropertyMember);
            if (source is { } property && AvaloniaRegisteredPropertyResolver.Resolve(context, declaredType ?? inherited, property.Text, target.Scope, property.Span) is { } resolved)
            {
                target.Annotations.Set(AvaloniaStyleAnnotations.SetterProperty, resolved);
                var parent = context.Ancestors.Skip(1).FirstOrDefault();
                if (parent != null && parent.Annotations.TryGet(AvaloniaStyleAnnotations.AssignedProperties, out var assigned) && !assigned.Add(resolved.Field))
                    context.Report("XG3107", "A style cannot assign the same registered property twice: " + property.Text, property.Span);
            }
        }
        // The directive wraps the inferred style metadata and is nearest to its setters.
        if (declaredType != null) target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, declaredType);
    }

    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        if (!AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.Setter)) return;
        // Property precedes Value regardless of XML attribute order: ISetterValue.Initialize
        // observes the registered property when the Value setter is called.
        var property = target.Assignments.OfType<BoundSetAssignment>().FirstOrDefault(a => a.Member.Name == AvaloniaStyleMetadata.PropertyMember);
        if (property != null) { target.Assignments.Remove(property); target.Assignments.Insert(0, property); }
    }

    private static BoundSelector? FindNestingSelector(BindingContext context, ObjectBindingBuilder current)
    {
        foreach (var ancestor in context.Ancestors)
        {
            if (ReferenceEquals(current, ancestor)) continue;
            if (ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.Selector, out var selector)) return selector;
            if (AvaloniaStyleScope.Is(ancestor.Type, AvaloniaStyleMetadata.ControlTheme) ||
                AvaloniaStyleScope.IsTemplate(ancestor.Type)) break;
        }
        return null;
    }

    private static INamedTypeSymbol? FindStyleOwner(BindingContext context, ObjectBindingBuilder current)
    {
        var parent = context.Ancestors.FirstOrDefault(ancestor => !ReferenceEquals(ancestor, current) && !AvaloniaStyleScope.Is(ancestor.Type, AvaloniaStyleMetadata.Styles));
        if (parent == null) return null;
        if (AvaloniaStyleScope.Is(parent.Type, AvaloniaStyleMetadata.StyledElement)) return parent.Type;
        if (AvaloniaStyleScope.Is(parent.Type, AvaloniaStyleMetadata.ControlTheme))
            context.Report("XG3116", "A Style inside a ControlTheme requires a selector.", current.Syntax.NameSpan);
        return null;
    }

    internal static INamedTypeSymbol? FindTarget(BindingContext context, ObjectBindingBuilder current)
    {
        foreach (var ancestor in context.Ancestors)
            if (!ReferenceEquals(current, ancestor) && ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var type)) return type;
        return null;
    }

    internal static (string Text, TextSpan Span)? TextMember(XamlElementSyntax element, string name)
    {
        var attribute = element.Attributes.FirstOrDefault(a => a.Name == name || a.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        if (attribute != null) return (attribute.Value, attribute.ValueSpan);
        var property = element.Children.OfType<XamlElementSyntax>().FirstOrDefault(e => e.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        if (property == null || property.Children.OfType<XamlElementSyntax>().Any()) return null;
        return (string.Concat(property.Children.OfType<XamlTextSyntax>().Select(t => t.Value)).Trim(), property.Span);
    }
}
