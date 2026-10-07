using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Preserves the framework's static-value owners, casing and whitespace rules.</summary>
public sealed class AvaloniaStaticLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        var name = targetType.MetadataName();
        var owner = name switch
        {
            "Avalonia.Media.TextTrimming" or "Avalonia.Controls.WindowTransparencyLevel" or "Avalonia.Styling.ThemeVariant" => targetType,
            "Avalonia.Media.TextDecorationCollection" => context.Types.Find("Avalonia.Media.TextDecorations"),
            _ => null
        };
        if (owner == null) return false;
        var theme = name == "Avalonia.Styling.ThemeVariant";
        var literal = theme ? text.Trim() : text;
        var selected = owner.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(property => property.IsStatic &&
            property.GetMethod != null && context.Types.IsAccessible(property.GetMethod) &&
            SymbolEqualityComparer.Default.Equals(property.Type, targetType) &&
            string.Equals(property.Name, literal, theme ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        if (selected == null) return false;
        context.Symbols.Add(new(span, selected, "static"));
        expression = new BoundStaticExpression(selected, selected.Type, span) { SourceInfoSpan = BoundSourceInfo.ValueLocation(context.Syntax, span) };
        return true;
    }
}
