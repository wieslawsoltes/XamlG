using XamlG.Runtime;
using XamlG.Runtime.Design;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class VisualEditTests
{
    [Fact]
    public void MultiPropertyGestureIsOneValidUndoTransaction()
    {
        var session = new XamlDocumentSession("<Root><Item Text='keep'/></Root>", "View.xaml");
        var tree = session.Current; var item = tree.Root!.DescendantsAndSelf().Single(e => e.Name == "Item");
        var source = new XamlSourceInfo(tree.Path, item.Span.Start, item.Span.Length, "item", "hash", version: tree.Version);
        var edit = new XamlVisualEdit(source, new Dictionary<string, string> { ["Width"] = "120", ["Height"] = "80", ["Canvas.Left"] = "24" });
        session.Apply(XamlBatchDesignerEdits.FromVisualEdit(tree, edit), true);
        Assert.Contains("Width=\"120\"", session.Current.Text); Assert.Contains("Text='keep'", session.Current.Text);
        Assert.Equal(tree.Text, session.Undo(1).Text);
    }
    [Fact]
    public void GesturesCannotTargetAStaleSourceRevision()
    {
        var tree = XamlSyntaxTree.Parse("<Root/>", "View.xaml", version: 2);
        var edit = new XamlVisualEdit(new("View.xaml", 0, 7, "root", "hash", version: 1), new Dictionary<string, string>());
        Assert.Throws<InvalidOperationException>(() => XamlBatchDesignerEdits.FromVisualEdit(tree, edit));
    }
    [Fact]
    public void ReparentPreservesNamespaceMeaningAndXmlWhitespace()
    {
        const string source = "<Root xmlns='one' xmlns:p='old'><A xml:space='preserve'><p:Item Text='same'/></A><B xmlns='two' xmlns:p='new'/></Root>";
        var tree = XamlSyntaxTree.Parse(source);
        var item = tree.Root!.DescendantsAndSelf().Single(e => e.LocalName == "Item");
        var parent = tree.Root.DescendantsAndSelf().Single(e => e.LocalName == "B");
        var result = tree.WithChanges(XamlDesignerEdits.Reparent(tree, item, parent).Changes, 0);
        var moved = result.Root!.DescendantsAndSelf().Single(e => e.LocalName == "Item");
        var scope = NamespaceScope.Empty;
        foreach (var ancestor in result.Root.DescendantsAndSelf().Where(e => e.Span.Contains(moved.Span)).OrderByDescending(e => e.Span.Length)) scope = scope.Push(ancestor);
        Assert.Equal("old", scope.Expand(moved.Name).Namespace); Assert.True(scope.PreserveSpace);
        Assert.Equal("one", scope.Expand("UnprefixedType").Namespace);
    }
}
