using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed class AvaloniaStyleObjectRule : IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target)
    {
        var inherited = FindTarget(context, target);
        if (target.Type.HasMetadataName(AvaloniaStyleMetadata.Style))
        {
            target.Annotations.Set(AvaloniaStyleAnnotations.AssignedProperties, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
            var source = TextMember(target.Syntax, AvaloniaStyleMetadata.SelectorMember);
            if (source is { } selector)
            {
                var syntax = AvaloniaSelectorParser.Parse(selector.Text, selector.Span, context.Diagnostics.Add, context.Cancellation);
                var bound = syntax == null ? null : new AvaloniaSelectorBinder(context, target.Scope, inherited).Bind(syntax);
                if (bound != null)
                {
                    target.Annotations.Set(AvaloniaStyleAnnotations.Selector, bound);
                    if (bound.TargetType != null) target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, bound.TargetType);
                }
            }
        }
        else if (target.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTheme) || target.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTemplate))
        {
            var source = TextMember(target.Syntax, AvaloniaStyleMetadata.TargetTypeMember);
            var type = source is { } explicitType
                ? (context.Values.BindText(explicitType.Text, context.Types.Find(ClrNames.Type)!, target.Scope, explicitType.Span) as BoundTypeExpression)?.ReferencedType as INamedTypeSymbol
                : inherited;
            if (type != null) target.Annotations.Set(AvaloniaStyleAnnotations.TargetType, type);
            if (target.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTheme))
                target.Annotations.Set(AvaloniaStyleAnnotations.AssignedProperties, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        }
        else if (target.Type.HasMetadataName(AvaloniaStyleMetadata.Setter))
        {
            var source = TextMember(target.Syntax, AvaloniaStyleMetadata.PropertyMember);
            if (source is not { } property) return;
            var resolved = AvaloniaRegisteredPropertyResolver.Resolve(context, inherited, property.Text, target.Scope, property.Span);
            if (resolved == null) return;
            target.Annotations.Set(AvaloniaStyleAnnotations.SetterProperty, resolved);
            var parent = context.Ancestors.Skip(1).FirstOrDefault();
            if (parent != null && parent.Annotations.TryGet(AvaloniaStyleAnnotations.AssignedProperties, out var assigned) && !assigned.Add(resolved.Field))
                context.Report("XG3107", "A style cannot assign the same registered property twice: " + property.Text, property.Span);
        }
    }

    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        if (!target.Type.HasMetadataName(AvaloniaStyleMetadata.Setter)) return;
        // Property precedes Value regardless of XML attribute order: ISetterValue.Initialize
        // observes the registered property when the Value setter is called.
        var property = target.Assignments.OfType<BoundSetAssignment>().FirstOrDefault(a => a.Member.Name == AvaloniaStyleMetadata.PropertyMember);
        if (property != null) { target.Assignments.Remove(property); target.Assignments.Insert(0, property); }
    }

    internal static INamedTypeSymbol? FindTarget(BindingContext context, ObjectBindingBuilder current)
    {
        foreach (var ancestor in context.Ancestors)
            if (!ReferenceEquals(current, ancestor) && ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var type)) return type;
        return null;
    }

    internal static (string Text, TextSpan Span)? TextMember(XamlElementSyntax element, string name)
    {
        var attribute = element.Attributes.FirstOrDefault(a => a.Name == name);
        if (attribute != null) return (attribute.Value, attribute.ValueSpan);
        var property = element.Children.OfType<XamlElementSyntax>().FirstOrDefault(e => e.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        if (property == null || property.Children.OfType<XamlElementSyntax>().Any()) return null;
        return (string.Concat(property.Children.OfType<XamlTextSyntax>().Select(t => t.Value)).Trim(), property.Span);
    }
}
