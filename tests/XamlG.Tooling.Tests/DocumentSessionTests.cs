using System.Collections.Immutable;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class DocumentSessionTests
{
    [Fact]
    public void UndoRedoUseMonotonicRevisions()
    {
        var session = new XamlDocumentSession("<Root Text='one'/>");
        var before = session.Current;
        session.Apply(XamlDesignerEdits.SetProperty(before, before.Root!, "Text", "two"));
        Assert.Equal(1, session.Current.Version);
        Assert.Equal(before.Text, session.Undo(1).Text);
        Assert.Equal(2, session.Current.Version);
        Assert.Contains("two", session.Redo(2).Text);
        Assert.Equal(3, session.Current.Version);
        Assert.Throws<InvalidOperationException>(() => session.Apply(XamlDesignerEdits.SetProperty(before, before.Root!, "Text", "stale")));
    }

    [Fact]
    public void DesignerOperationsPreserveCommentsAndExistingQuotes()
    {
        var session = new XamlDocumentSession("<!--keep--><Root Text='one'/>");
        var tree = session.Current;
        session.Apply(XamlDesignerEdits.InsertChild(tree, tree.Root!, "<Child Value='1'/>") , true);
        Assert.Contains("<!--keep--><Root Text='one'>", session.Current.Text);
        Assert.False(session.Current.HasErrors);
        var child = session.Current.Root!.Children.OfType<XamlElementSyntax>().Single();
        session.Apply(XamlDesignerEdits.RemoveElement(session.Current, child), true);
        Assert.Empty(session.Current.Root!.Children.OfType<XamlElementSyntax>());
    }

    [Fact]
    public void InvalidDesignerTransactionLeavesHistoryUntouched()
    {
        var session = new XamlDocumentSession("<Root/>");
        var transaction = new XamlEditTransaction(0, "bad", ImmutableArray.Create(new XamlTextChange(new(0, 7), "<Root")));
        Assert.Throws<InvalidOperationException>(() => session.Apply(transaction, true));
        Assert.Equal(0, session.Current.Version);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ReparentCannotIntroduceCycles()
    {
        var tree = XamlSyntaxTree.Parse("<Root><A><B/></A></Root>");
        var nodes = tree.Root!.DescendantsAndSelf().ToArray();
        Assert.Throws<InvalidOperationException>(() => XamlDesignerEdits.Reparent(tree, nodes[1], nodes[2]));
    }

    [Fact]
    public void HistoryHasABoundedCharacterBudget()
    {
        var session = new XamlDocumentSession("<Root A='1'/>", historyCapacity: 2);
        for (var i = 0; i < 5; i++)
        { var tree = session.Current; session.Apply(XamlDesignerEdits.SetProperty(tree, tree.Root!, "A", i.ToString())); }
        session.Undo(session.Current.Version);
        session.Undo(session.Current.Version);
        Assert.False(session.CanUndo);
        Assert.True(session.CanRedo);
    }
}
