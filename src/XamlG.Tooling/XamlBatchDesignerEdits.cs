using System.Collections.Immutable;
using XamlG.Runtime.Design;
using XamlG.Syntax;
using XamlG.Tooling.Refactoring;

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

    /// <summary>Plans one source-preserving transaction per document for a group gesture
    /// or arrangement. Every visual must still name its exact original syntax snapshot.</summary>
    public static ImmutableArray<XamlDocumentEdits> FromVisualEdits(IReadOnlyDictionary<string, XamlSyntaxTree> documents,
        IReadOnlyList<XamlVisualEdit> edits)
    {
        if (edits.Count is < 1 or > 256) throw new ArgumentException("Supply one to 256 visual edits.", nameof(edits));
        var result = ImmutableArray.CreateBuilder<XamlDocumentEdits>();
        foreach (var group in edits.GroupBy(edit => edit.Source.Path, StringComparer.Ordinal))
        {
            if (!documents.TryGetValue(group.Key, out var tree)) throw new InvalidOperationException("The visual source document no longer exists: " + group.Key);
            var nodes = new HashSet<(int Start, int Length)>();
            var changes = ImmutableArray.CreateBuilder<XamlTextChange>();
            foreach (var edit in group)
            {
                if (!nodes.Add((edit.Source.Start, edit.Source.Length))) throw new InvalidOperationException("Several selected instances refer to the same XAML element. Edit that source element directly.");
                if (edit.Properties.Count > 256) throw new ArgumentException("Too many properties in a visual edit.");
                changes.AddRange(FromVisualEdit(tree, edit).Changes);
            }
            var ordered = changes.OrderBy(change => change.Span.Start).ToImmutableArray();
            var previousEnd = -1; var previousStart = -1;
            foreach (var change in ordered)
            {
                if (change.Span.Start < previousEnd || change.Span.Start == previousStart) throw new InvalidOperationException("The selected visuals produce conflicting source edits.");
                previousStart = change.Span.Start; previousEnd = change.Span.End;
            }
            if (!ordered.IsEmpty) result.Add(new(tree.Path, tree.Text, tree.Version, ordered));
        }
        return result.ToImmutable();
    }
}
