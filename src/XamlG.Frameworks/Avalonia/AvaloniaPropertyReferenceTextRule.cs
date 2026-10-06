using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Resolves registered-property literals against the semantic owner, not the
/// immediate visual receiving the binding. Explicit qualified owners remain authoritative.</summary>
public sealed class AvaloniaPropertyReferenceTextRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (!targetType.HasMetadataName(AvaloniaLiteralMetadata.Property)) return false;
        var current = context.Ancestors.FirstOrDefault();
        INamedTypeSymbol? owner = null;
        if (current?.Type.HasMetadataName(AvaloniaLiteralMetadata.TemplateBinding) == true)
        {
            INamedTypeSymbol? styleFallback = null;
            foreach (var ancestor in context.Ancestors)
            {
                if (ReferenceEquals(ancestor, current)) continue;
                // Use the closest semantic boundary, including a selector entering a
                // nested template. An unknown selector owner must remain unknown.
                if (ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.Selector, out var selector) && selector.HasTemplateScope)
                { owner = selector.TemplateOwnerType; break; }
                if (ancestor.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTemplate) ||
                    ancestor.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTheme))
                {
                    if (ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var templateType)) owner = templateType;
                    break;
                }
                if (styleFallback == null && ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var styleType))
                    styleFallback = styleType;
            }
            // A standalone Style has no enclosing theme or concrete ControlTemplate.
            // Only use its target when there was no intervening template boundary.
            if (owner == null && !context.Ancestors.Any(ancestor =>
                ancestor.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTemplate) ||
                ancestor.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTheme) ||
                ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.Selector, out var selector) && selector.HasTemplateScope))
                owner = styleFallback;
        }
        else if (current != null)
        {
            var animatable = context.Types.Find(AvaloniaLiteralMetadata.Animatable);
            foreach (var ancestor in context.Ancestors)
            {
                if (ReferenceEquals(ancestor, current)) continue;
                if (ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var declared))
                { owner = declared; break; }
                if (animatable != null && context.Types.Compilation.ClassifyCommonConversion(ancestor.Type, animatable).IsImplicit)
                { owner = ancestor.Type; break; }
                if (ancestor.Type.HasMetadataName(AvaloniaStyleMetadata.Style) ||
                    ancestor.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTheme) ||
                    ancestor.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTemplate)) break;
            }
        }
        // Constructor ranking is speculative: an unsuccessful conversion must not
        // poison another overload candidate with diagnostics or a runtime fallback.
        var property = AvaloniaRegisteredPropertyResolver.Resolve(context, owner, text, scope, span, report: false);
        if (property != null) expression = property.Reference(span);
        return true;
    }
}
