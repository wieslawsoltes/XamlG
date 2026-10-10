using XamlG.ProjectSystem;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class WorkspaceForkTests
{
    [Fact]
    public void Fork_shares_immutable_source_and_preserves_revision_and_startup_without_publishing_edits()
    {
        var workspace = WorkspaceTemplates.CreateSolution("Demo", new("console", "App")).CreateWorkspace();
        workspace.SetStartupProject(0, "App/App.csproj");
        var original = workspace.Current;
        var fork = workspace.Fork();
        Assert.Same(original, fork.Current);
        fork.Apply(original.Revision, [ProjectFileEditor.SetProperty(fork.Current, "App/App.csproj", "RootNamespace", "Forked")]);
        Assert.Same(original, workspace.Current);
        Assert.Equal(original.Revision + 1, fork.Current.Revision);
        Assert.Equal(original.StartupProject, fork.Current.StartupProject);
        Assert.NotEqual(original.Files["App/App.csproj"], fork.Current.Files["App/App.csproj"]);
    }

    [Fact]
    public void Entry_changes_and_conflicting_transactions_are_isolated_from_the_source_workspace()
    {
        var workspace = WorkspaceTemplates.CreateSolution("Demo", new("console", "App")).CreateWorkspace();
        workspace.SetStartupProject(0, "App/App.csproj");
        var original = workspace.Current;
        var fork = workspace.Fork();
        fork.Open(original.Revision, "App/App.csproj");
        Assert.Null(fork.Current.StartupProject);
        Assert.Equal("Demo.slnx", workspace.Current.EntryPath);
        Assert.Equal("App/App.csproj", workspace.Current.StartupProject);
        Assert.Throws<InvalidOperationException>(() => fork.Apply(original.Revision, [new("File.cs", null, new("class File;"))]));
        Assert.Same(original, workspace.Current);
    }
}
