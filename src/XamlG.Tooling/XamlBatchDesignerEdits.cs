using System.Collections.Immutable;
using XamlG.Runtime.Design;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>Coalesces newly inserted attributes at the same source position into one atomic edit.</summary>
public static class XamlBatchDesignerEdits
{
    public static XamlEditTransaction SetProperties(XamlSyntaxTree tree, XamlElementSyntax element,
        IReadOnlyDictionary<string, string> properties, string description = "Edit visual properties")
    {
        var planned = properties.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => XamlSyntaxEditor.SetAttribute(tree, element, p.Key, p.Value)).ToArray();
        var edits = ImmutableArray.CreateBuilder<XamlTextChange>();
        foreach (var group in planned.GroupBy(c => c.Span).OrderBy(g => g.Key.Start))
        {
            if (group.Key.Length != 0 && group.Count() != 1) throw new InvalidOperationException("A visual transaction contains conflicting property edits.");
            edits.Add(new(group.Key, string.Concat(group.Select(c => c.NewText))));
        }
        return new(tree.Version, description, edits.ToImmutable());
    }

    public static XamlEditTransaction FromVisualEdit(XamlSyntaxTree tree, XamlVisualEdit edit)
    {
        if (tree.Version != edit.Source.Version || tree.Path != edit.Source.Path)
            throw new InvalidOperationException("The visual gesture belongs to an older or different source snapshot.");
        var element = tree.Root?.DescendantsAndSelf().FirstOrDefault(e => e.Span.Start == edit.Source.Start && e.Span.Length == edit.Source.Length)
            ?? throw new InvalidOperationException("The visual source node no longer exists.");
        return SetProperties(tree, element, edit.Properties);
    }
}
