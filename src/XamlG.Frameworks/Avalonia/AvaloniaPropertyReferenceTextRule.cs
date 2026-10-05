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
            // A nested Style can select a template child. TemplateBinding still refers
            // to the nearest ControlTemplate's owner, not that Style's selector type.
            var template = context.Ancestors.FirstOrDefault(ancestor => ancestor.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTemplate));
            if (template != null && template.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var templateType)) owner = templateType;
        }
        else if (current != null) owner = AvaloniaStyleObjectRule.FindTarget(context, current);
        // Conversion is also used speculatively when ranking constructors. Failure must
        // not append diagnostics that would poison another successful overload candidate.
        var property = AvaloniaRegisteredPropertyResolver.Resolve(context, owner, text, scope, span, report: false);
        if (property != null) expression = property.Reference(span);
        return true;
    }
}
