using System.Collections.Immutable;
namespace XamlG.Syntax;

/// <summary>Plans minimal source edits; the caller applies the resulting transaction to its original revision.</summary>
public static class XamlSyntaxEditor
{
    public static XamlTextChange SetAttribute(XamlSyntaxTree tree, XamlElementSyntax element, string name, string value)
    {
        EnsureOwned(tree, element); ValidateName(name);
        var attribute = element.Attributes.FirstOrDefault(a => a.Name == name);
        if (attribute != null) return new(attribute.ValueSpan, Escape(value, attribute.Quote));
        var offset = element.OpenTagSpan.End - (element.IsSelfClosing ? 2 : 1);
        if (offset < element.NameSpan.End) throw new InvalidOperationException("The opening tag is incomplete.");
        return new(new(offset, 0), " " + name + "=\"" + Escape(value, '"') + "\"");
    }
    public static ImmutableArray<XamlTextChange> RenameElement(XamlSyntaxTree tree, XamlElementSyntax element, string name)
    {
        EnsureOwned(tree, element); ValidateName(name); var changes = ImmutableArray.CreateBuilder<XamlTextChange>(); changes.Add(new(element.NameSpan, name));
        if (element.EndNameSpan.Length > 0) changes.Add(new(element.EndNameSpan, name));
        return changes.ToImmutable();
    }
    public static XamlTextChange RemoveAttribute(XamlSyntaxTree tree, XamlElementSyntax element, string name)
    {
        EnsureOwned(tree, element); var attribute = element.Attributes.FirstOrDefault(a => a.Name == name) ?? throw new ArgumentException("Attribute does not exist.", nameof(name));
        var start = attribute.Span.Start; while (start > element.NameSpan.End && (tree.Text[start - 1] == ' ' || tree.Text[start - 1] == '\t')) start--;
        return new(TextSpan.FromBounds(start, attribute.Span.End), string.Empty);
    }
    public static XamlTextChange ReplaceElement(XamlSyntaxTree tree, XamlElementSyntax element, string markup)
    {
        EnsureOwned(tree, element); var parsed = XamlSyntaxTree.Parse(markup);
        if (parsed.HasErrors || parsed.Root == null) throw new ArgumentException("Replacement must be one well-formed element.", nameof(markup));
        return new(element.Span, markup);
    }
    public static string Escape(string value, char quote) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\r", "&#13;").Replace("\n", "&#10;").Replace("\t", "&#9;").Replace(quote.ToString(), quote == '\'' ? "&apos;" : "&quot;");
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or '=' or '/' or '\'' or '"')) throw new ArgumentException("Invalid XML name.", nameof(name));
    }
    private static void EnsureOwned(XamlSyntaxTree tree, XamlElementSyntax element)
    {
        if (tree.Root == null || !tree.Root.DescendantsAndSelf().Any(n => ReferenceEquals(n, element))) throw new InvalidOperationException("The node does not belong to this source snapshot.");
    }
}
