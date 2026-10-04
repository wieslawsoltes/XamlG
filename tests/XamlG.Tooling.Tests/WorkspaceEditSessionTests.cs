using System.Collections.Immutable;
using XamlG.Syntax;
using XamlG.Tooling.Editing;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class WorkspaceEditSessionTests
{
    private static XamlWorkspaceEditSession Create(int history = 128) => new(new Dictionary<string, string>
    { ["View.axaml"] = "<View Name='old'/>", ["Code.cs"] = "void Method() { old.Focus(); }" }, historyCapacity: history);
    private static XamlDocumentEdits Rename(string path, string text) => new(path, text, null,
        ImmutableArray.Create(new XamlTextChange(new(text.IndexOf("old", StringComparison.Ordinal), 3), "renamed")));

    [Fact]
    public void MultiLanguageRenameAndUndoRedoPublishOneSnapshot()
    {
        var session = Create(); var before = session.Current;
        var result = session.Apply(before.Revision, before.Documents.Select(p => Rename(p.Key, p.Value)), "Rename old");
        Assert.Equal(before.Revision + 1, result.Revision);
        Assert.All(result.Documents, p => Assert.Contains("renamed", p.Value));
        Assert.Equal("Rename old", session.UndoDescription);
        var undone = session.Undo(result.Revision);
        Assert.Equal(before.Documents, undone.Documents); Assert.True(undone.Revision > result.Revision);
        var redone = session.Redo(undone.Revision);
        Assert.Equal(result.Documents, redone.Documents); Assert.True(redone.Revision > undone.Revision);
    }
    [Fact]
    public void AStaleSecondFileNeverPartiallyChangesTheFirst()
    {
        var session = Create(); var before = session.Current;
        Assert.Throws<InvalidOperationException>(() => session.Apply(before.Revision, new[]
        { Rename("View.axaml", before.Documents["View.axaml"]), Rename("Code.cs", "stale old") }, "Rename"));
        Assert.Same(before, session.Current); Assert.False(session.CanUndo);
    }
    [Fact]
    public void ValidatorFailureAndReentrantChangesAreAtomic()
    {
        var session = Create(); var before = session.Current;
        var edits = before.Documents.Select(p => Rename(p.Key, p.Value)).ToArray();
        Assert.Throws<ArgumentException>(() => session.Apply(0, edits, "Rename", _ => throw new ArgumentException("invalid")));
        Assert.Same(before, session.Current);
        Assert.Throws<InvalidOperationException>(() => session.Apply(0, edits, "Rename", _ =>
            session.ReplaceAll(0, before.Documents.SetItem("Code.cs", "new user input"), "Concurrent edit")));
        Assert.Equal("new user input", session.Current.Documents["Code.cs"]);
        Assert.Equal(before.Documents["View.axaml"], session.Current.Documents["View.axaml"]);
    }
    [Fact]
    public void ConflictingRangesAndSplitSurrogatesAreRejected()
    {
        var session = new XamlWorkspaceEditSession(new Dictionary<string, string> { ["View.xaml"] = "😀abc" });
        var bad = new XamlDocumentEdits("View.xaml", "😀abc", null,
            ImmutableArray.Create(new XamlTextChange(new(1, 1), "x")));
        Assert.Throws<InvalidOperationException>(() => session.Apply(0, new[] { bad }, "Bad edit"));
        var overlap = bad with { Changes = ImmutableArray.Create(new XamlTextChange(new(2, 2), "x"), new XamlTextChange(new(3, 1), "y")) };
        Assert.Throws<InvalidOperationException>(() => session.Apply(0, new[] { overlap }, "Overlap"));
        Assert.Equal(0, session.Current.Revision);
    }
    [Fact]
    public void NewEditsInvalidateRedoAndHistoryIsBounded()
    {
        var session = Create(1); var first = session.Current;
        session.ReplaceAll(0, first.Documents.SetItem("Code.cs", "first"), "First");
        session.ReplaceAll(1, first.Documents.SetItem("Code.cs", "second"), "Second");
        session.Undo(2); Assert.False(session.CanUndo); Assert.True(session.CanRedo);
        session.ReplaceAll(3, first.Documents.SetItem("Code.cs", "replacement"), "New edit");
        Assert.False(session.CanRedo);
        Assert.Throws<InvalidOperationException>(() => session.Undo(1));
    }
    [Fact]
    public void AddsAndDeletesAreUndoneTogetherAndNoOpsRetainIdentity()
    {
        var session = Create(); var before = session.Current;
        Assert.Same(before, session.ReplaceAll(0, before.Documents, "No change"));
        var result = session.ReplaceAll(0, before.Documents.Remove("Code.cs").Add("Resources.axaml", "<Resources/>"), "Structure");
        Assert.Equal(before.Documents, session.Undo(result.Revision).Documents);
    }
    [Fact]
    public void BudgetFailureRetainsHistoryAndCurrentBuffers()
    {
        var session = new XamlWorkspaceEditSession(new Dictionary<string, string> { ["A.xaml"] = "<A/>" }, maximumDocuments: 1, maximumCharacters: 16);
        Assert.Throws<InvalidOperationException>(() => session.ReplaceAll(0, new Dictionary<string, string> { ["A.xaml"] = "<A/>", ["B.xaml"] = "<B/>" }, "Oversized"));
        Assert.Single(session.Current.Documents); Assert.False(session.CanUndo);
    }
}
