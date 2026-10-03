using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>Source-first designer operations. Existing comments, namespace spelling and attribute formatting are retained.</summary>
public static class XamlDesignerEdits
{
    public static XamlEditTransaction SetProperty(XamlSyntaxTree tree, XamlElementSyntax element, string name, string value) =>
        new(tree.Version, "Set " + name, ImmutableArray.Create(XamlSyntaxEditor.SetAttribute(tree, element, name, value)));

    public static XamlEditTransaction RemoveProperty(XamlSyntaxTree tree, XamlElementSyntax element, string name) =>
        new(tree.Version, "Remove " + name, ImmutableArray.Create(XamlSyntaxEditor.RemoveAttribute(tree, element, name)));

    public static XamlEditTransaction RenameElement(XamlSyntaxTree tree, XamlElementSyntax element, string name) =>
        new(tree.Version, "Change element type", XamlSyntaxEditor.RenameElement(tree, element, name));

    public static XamlEditTransaction InsertChild(XamlSyntaxTree tree, XamlElementSyntax parent, string markup, int index = -1)
    {
        ValidateOwned(tree, parent);
        ValidateMarkup(markup);
        return new(tree.Version, "Insert child", ImmutableArray.Create(Insert(tree, parent, markup, index)));
    }

    public static XamlEditTransaction RemoveElement(XamlSyntaxTree tree, XamlElementSyntax element)
    {
        ValidateOwned(tree, element);
        if (ReferenceEquals(tree.Root, element)) throw new InvalidOperationException("The document root cannot be removed.");
        return new(tree.Version, "Remove element", ImmutableArray.Create(new XamlTextChange(element.Span, string.Empty)));
    }

    public static XamlEditTransaction Reparent(XamlSyntaxTree tree, XamlElementSyntax element, XamlElementSyntax parent, int index = -1)
    {
        ValidateOwned(tree, element);
        ValidateOwned(tree, parent);
        if (ReferenceEquals(tree.Root, element) || element.Span.Contains(parent.Span))
            throw new InvalidOperationException("An element cannot be moved into itself or one of its descendants.");
        var markup = tree.Text.Substring(element.Span.Start, element.Span.Length);
        var insertion = Insert(tree, parent, markup, index);
        if (element.Span.Start <= insertion.Span.Start && insertion.Span.Start <= element.Span.End)
            return new(tree.Version, "Move element", ImmutableArray<XamlTextChange>.Empty);
        return new(tree.Version, "Move element", ImmutableArray.Create(new XamlTextChange(element.Span, string.Empty), insertion));
    }

    private static XamlTextChange Insert(XamlSyntaxTree tree, XamlElementSyntax parent, string markup, int index)
    {
        var children = parent.Children.OfType<XamlElementSyntax>().ToArray();
        if (index < -1 || index > children.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var newline = tree.Text.Contains("\r\n") ? "\r\n" : "\n";
        var indentation = Indentation(tree.Text, parent.Span.Start);
        var body = newline + indentation + "    " + markup;
        if (parent.IsSelfClosing)
            return new(new(parent.OpenTagSpan.End - 2, 2), ">" + body + newline + indentation + "</" + parent.Name + ">");
        if (parent.CloseTagSpan.Length == 0) throw new InvalidOperationException("The parent must have a complete closing tag.");
        var offset = index >= 0 && index < children.Length ? children[index].Span.Start : parent.CloseTagSpan.Start;
        return new(new(offset, 0), body + newline + indentation);
    }

    private static string Indentation(string source, int offset)
    {
        var start = offset;
        while (start > 0 && source[start - 1] is not '\r' and not '\n') start--;
        var end = start;
        while (end < offset && source[end] is ' ' or '\t') end++;
        return source.Substring(start, end - start);
    }

    private static void ValidateMarkup(string markup)
    {
        var syntax = XamlSyntaxTree.Parse(markup);
        if (syntax.HasErrors) throw new ArgumentException("The inserted fragment must contain one well-formed element.", nameof(markup));
    }

    private static void ValidateOwned(XamlSyntaxTree tree, XamlElementSyntax element)
    {
        if (tree.Root == null || !tree.Root.DescendantsAndSelf().Any(n => ReferenceEquals(n, element)))
            throw new InvalidOperationException("The node belongs to another source revision.");
    }
}
