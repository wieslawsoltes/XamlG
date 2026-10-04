using XamlG.Tooling.Editing;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class FileRenameTests
{
    [Theory]
    [InlineData("Values.axaml", "New.axaml")]
    [InlineData("./Values.axaml", "./New.axaml")]
    [InlineData("/Values.axaml", "/New.axaml")]
    [InlineData("xamlg://filemoves/Values.axaml", "xamlg://filemoves/New.axaml")]
    public void IncomingReferencesKeepTheirAddressingStyle(string original, string expected)
    {
        var fixture = new FileMoveFixture(("Main.axaml", FileMoveFixture.Child(original)), ("Values.axaml", string.Empty));
        var plan = fixture.Plan(FileMoveFixture.Move("Values.axaml", "New.axaml"));
        Assert.Contains("Source='" + expected + "'", FileMoveFixture.Text(plan, "Main.axaml"));
        Assert.Equal(FileMoveFixture.Physical("Main.axaml"), Assert.Single(plan.Documents).Path);
        fixture.AssertEmits(plan);
    }
    [Fact]
    public void MovingTheCallerRebasesItsOutgoingRelativeReferences()
    {
        var fixture = new FileMoveFixture(("Main.axaml", FileMoveFixture.Child("Resources/Values.axaml")), ("Resources/Values.axaml", string.Empty));
        var plan = fixture.Plan(FileMoveFixture.Move("Main.axaml", "Views/Main.axaml"));
        Assert.Contains("Source='../Resources/Values.axaml'", FileMoveFixture.Text(plan, "Views/Main.axaml"));
        Assert.Equal(FileMoveFixture.Physical("Main.axaml"), Assert.Single(plan.Documents).Path);
        fixture.AssertEmits(plan);
    }
    [Fact]
    public void SimultaneousMovesAreNotAppliedAsASequence()
    {
        var fixture = new FileMoveFixture(("Main.axaml", FileMoveFixture.Child("Values.axaml")), ("Values.axaml", string.Empty));
        var plan = fixture.Plan(FileMoveFixture.Move("Main.axaml", "Area/Main.axaml"), FileMoveFixture.Move("Values.axaml", "Area/Values.axaml"));
        Assert.Empty(plan.Documents);
        Assert.Contains("Source='Values.axaml'", FileMoveFixture.Text(plan, "Area/Main.axaml"));
        fixture.AssertEmits(plan);
    }
    [Fact]
    public void SwappedNamesPreserveTheOriginalResourceEdges()
    {
        var fixture = new FileMoveFixture(("Main.axaml", FileMoveFixture.Child("Values.axaml")), ("Values.axaml", "<Item.Text>resource</Item.Text>"));
        var plan = fixture.Plan(FileMoveFixture.Move("Main.axaml", "Values.axaml"), FileMoveFixture.Move("Values.axaml", "Main.axaml"));
        Assert.Contains("Source='Main.axaml'", FileMoveFixture.Text(plan, "Values.axaml"));
        Assert.Contains("resource", FileMoveFixture.Text(plan, "Main.axaml"));
    }
    [Fact]
    public void PropertyElementSourceRetainsWhitespaceAndComments()
    {
        var fixture = new FileMoveFixture(("Main.axaml", "<Item.Child><Include><Include.Source>\n  Values.axaml  \n</Include.Source><!--keep--></Include></Item.Child>"), ("Values.axaml", string.Empty));
        var plan = fixture.Plan(FileMoveFixture.Move("Values.axaml", "New & Values.axaml"));
        var result = FileMoveFixture.Text(plan, "Main.axaml");
        Assert.Contains("\n  New%20%26%20Values.axaml  \n", result);
        Assert.Contains("<!--keep-->", result);
    }
    [Fact]
    public void UnrelatedLiteralTextIsNeverRenamed()
    {
        var fixture = new FileMoveFixture(("Main.axaml", "<Item.Text>Values.axaml</Item.Text>" + FileMoveFixture.Child("Values.axaml")), ("Values.axaml", string.Empty));
        var plan = fixture.Plan(FileMoveFixture.Move("Values.axaml", "New.axaml"));
        Assert.Contains("<Item.Text>Values.axaml</Item.Text>", FileMoveFixture.Text(plan, "Main.axaml"));
    }
    [Fact]
    public void DestinationAndDuplicateSourceCollisionsAreRejected()
    {
        var fixture = new FileMoveFixture(("A.axaml", string.Empty), ("B.axaml", string.Empty));
        Assert.Throws<InvalidOperationException>(() => fixture.Plan(FileMoveFixture.Move("A.axaml", "B.axaml")));
        Assert.Throws<ArgumentException>(() => fixture.Plan(FileMoveFixture.Move("A.axaml", "C.axaml"), FileMoveFixture.Move("A.axaml", "D.axaml")));
    }
    [Fact]
    public void FileIdentityAndAllSourceEditsFormOneUndoUnit()
    {
        var fixture = new FileMoveFixture(("Main.axaml", FileMoveFixture.Child("Values.axaml")), ("Values.axaml", string.Empty));
        var source = fixture.Compiler.ProjectDocuments.ToDictionary(d => d.Syntax.Path, d => d.Syntax.Text);
        source["Code.cs"] = "class Unchanged { }";
        var session = new XamlWorkspaceEditSession(source);
        var before = session.Current;
        var plan = fixture.Plan(FileMoveFixture.Move("Values.axaml", "New.axaml"));
        var after = session.ApplyFileRename(0, plan);
        Assert.Equal(1, after.Revision);
        Assert.False(after.Documents.ContainsKey(FileMoveFixture.Physical("Values.axaml")));
        Assert.Contains("New.axaml", after.Documents[FileMoveFixture.Physical("Main.axaml")]);
        Assert.Equal(source, session.Undo(1).Documents);
        Assert.Equal(after.Documents, session.Redo(2).Documents);
    }
    [Fact]
    public void AStalePlanOrFailingValidatorCannotPartiallyMoveFiles()
    {
        var fixture = new FileMoveFixture(("Main.axaml", FileMoveFixture.Child("Values.axaml")), ("Values.axaml", string.Empty));
        var source = fixture.Compiler.ProjectDocuments.ToDictionary(d => d.Syntax.Path, d => d.Syntax.Text);
        var session = new XamlWorkspaceEditSession(source);
        var plan = fixture.Plan(FileMoveFixture.Move("Values.axaml", "New.axaml"));
        Assert.Throws<InvalidOperationException>(() => session.ApplyFileRename(0, plan, _ => throw new InvalidOperationException("rejected")));
        Assert.Equal(0, session.Current.Revision); Assert.False(session.CanUndo);
        source[FileMoveFixture.Physical("Main.axaml")] += " ";
        session.ReplaceAll(0, source, "Concurrent edit");
        Assert.Throws<InvalidOperationException>(() => session.ApplyFileRename(0, plan));
        Assert.Throws<InvalidOperationException>(() => session.ApplyFileRename(1, plan));
        Assert.True(session.Current.Documents.ContainsKey(FileMoveFixture.Physical("Values.axaml")));
    }
}
