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
        var scopeKind = member?.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.HasMetadataName("Avalonia.Metadata.InheritDataTypeFromAttribute") == true)
            ?.ConstructorArguments.FirstOrDefault().Value as int?;
        if (scopeKind == 2 || current?.Type.HasMetadataName(AvaloniaLiteralMetadata.TemplateBinding) == true)
        {
            owner = Bindings.BindingTargetTypeResolver.TemplateTarget(context, includeDetached: true);
        }
        else if (current != null)
        {
            foreach (var ancestor in context.Ancestors)
            {
                if (scopeKind == 1)
                {
                    if (ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.SetterScope, out var setterScope))
                    { owner = setterScope.TargetType; break; }
                    continue;
                }
                if (ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.DetachedTemplateTarget, out var detached))
                { owner = detached; break; }
                if (ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var declared))
                { owner = declared; break; }
                for (var propertyScope = context.PropertyScope; propertyScope != null; propertyScope = propertyScope.Parent)
                    if (ReferenceEquals(propertyScope.Target, ancestor) && propertyScope.Member.Getter?.ReturnType.HasMetadataName("Avalonia.Animation.Transitions") == true)
                    { owner = ancestor.Type; break; }
                if (owner != null) break;
                if (AvaloniaStyleScope.Is(ancestor.Type, AvaloniaStyleMetadata.Style) ||
                    AvaloniaStyleScope.Is(ancestor.Type, AvaloniaStyleMetadata.ControlTheme) ||
                    AvaloniaStyleScope.IsTemplate(ancestor.Type)) break;
            }
        }
        if (owner == null) return true;
        // Constructor ranking is speculative: an unsuccessful conversion must not
        // poison another overload candidate with diagnostics or a runtime fallback.
        var property = AvaloniaRegisteredPropertyResolver.Resolve(context, owner, text, scope, span, report: false);
        if (property != null) expression = property.Reference(span);
        return true;
    }
}
