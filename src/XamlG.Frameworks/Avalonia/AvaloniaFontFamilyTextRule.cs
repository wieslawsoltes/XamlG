using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Font-family literals retain the document URI used for relative asset names.
/// The compiler resolves constructor and service symbols; it never loads a font.</summary>
public sealed class AvaloniaFontFamilyTextRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (!targetType.HasMetadataName(AvaloniaLiteralMetadata.FontFamily)) return false;
        var constructor = ((INamedTypeSymbol)targetType).InstanceConstructors.FirstOrDefault(method => method.Parameters.Length == 2 &&
            method.Parameters[0].Type.HasMetadataName(ClrNames.Uri) && method.Parameters[1].Type.SpecialType == SpecialType.System_String && context.Types.IsAccessible(method));
        var uriContext = context.Types.Find(AvaloniaMetadata.UriContext);
        var baseUri = uriContext?.GetMembers(AvaloniaLiteralMetadata.BaseUri).OfType<IPropertySymbol>().FirstOrDefault(property => property.GetMethod != null && context.Types.IsAccessible(property.GetMethod));
        if (constructor == null || uriContext == null || baseUri == null)
        {
            context.Report("XG3001", "The font-family constructor or URI-context contract is unavailable.", span);
            return true;
        }
        var sourceUri = new BoundPropertyAccessExpression(new BoundServiceExpression(uriContext, span), baseUri,
            ImmutableArray<BoundExpression>.Empty, span);
        expression = new BoundNewExpression(constructor, ImmutableArray.Create<BoundExpression>(sourceUri,
            new BoundConstantExpression(text, constructor.Parameters[1].Type, span)), span);
        return true;
    }
}
