using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

/// <summary>Reads selectors before general property normalization and object conversion.</summary>
internal sealed record AvaloniaSelectorSource(string Text, TextSpan Span, NamespaceScope Scope)
{
    public static AvaloniaSelectorSource? Read(BindingContext context, ObjectBindingBuilder target)
    {
        const string name = AvaloniaStyleMetadata.SelectorMember;
        var attribute = target.Syntax.Attributes.FirstOrDefault(a => a.Name == name || a.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        if (attribute != null)
        {
            if (attribute.Value.StartsWith("{}", StringComparison.Ordinal))
                return new(attribute.Value.Substring(2), new(attribute.ValueSpan.Start + 2, attribute.ValueSpan.Length - 2), target.Scope);
            if (!attribute.Value.StartsWith("{", StringComparison.Ordinal))
                return new(attribute.Value, attribute.ValueSpan, target.Scope);
            context.Report("XG3100", "Selector requires a text value.", attribute.ValueSpan);
            return null;
        }
        var property = target.Syntax.Children.OfType<XamlElementSyntax>().FirstOrDefault(e => e.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        if (property == null) return null;
        var values = property.Children.Where(node => node is XamlTextSyntax or XamlElementSyntax).ToArray();
        if (values.Length == 1 && values[0] is XamlTextSyntax text)
            return new(text.Value, text.Span, target.Scope.Push(property));
        context.Report("XG3100", "Selector requires exactly one text value.", property.Span);
        return null;
    }
}
