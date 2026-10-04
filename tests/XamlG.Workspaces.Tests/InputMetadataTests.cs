using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class InputMetadataTests
{
    private const string Source = "<View xmlns='clr-namespace:Inputs'/>";

    [Fact]
    public async Task LinkedDocumentUsesItsEvaluatedLogicalIdentityRatherThanAnExternalPath()
    {
        using var workspace = new AdhocWorkspace();
        var root = Path.GetFullPath("Project");
        var external = Path.GetFullPath("Shared/Palette.axaml");
        var project = Create(workspace, root)
            .AddAdditionalDocument("Palette.axaml", SourceText.From(Source), filePath: external).Project;
        project = Configure(project, root, external, "build_metadata.AdditionalFiles.XamlGLogicalPath = Resources/Palette.axaml");
        var loaded = await XamlWorkspaceProjectLoader.LoadAsync(project);
        var document = Assert.Single(loaded.Compiler.ProjectDocuments);
        Assert.Equal(external, document.Syntax.Path);
        Assert.Equal("Resources/Palette.axaml", document.LogicalPath);
        var analysis = Assert.Single(loaded.Analyze());
        Assert.True(analysis.Output.Success, string.Join("\n", analysis.Output.Diagnostics));
        Assert.Equal("xamlg://inputs/Resources/Palette.axaml", analysis.Document.Options.ResourceUri);
    }

    [Fact]
    public async Task DisabledAdditionalFilesDoNotParticipateInTheProjectCatalog()
    {
        using var workspace = new AdhocWorkspace();
        var root = Path.GetFullPath("Project");
        var disabled = Path.Combine(root, "Ignored.axaml");
        var project = Create(workspace, root)
            .AddAdditionalDocument("Ignored.axaml", SourceText.From("<NotARealType/>"), filePath: disabled).Project
            .AddAdditionalDocument("View.axaml", SourceText.From(Source), filePath: Path.Combine(root, "View.axaml")).Project;
        project = Configure(project, root, disabled, "build_metadata.AdditionalFiles.XamlGCompile = false");
        var loaded = await XamlWorkspaceProjectLoader.LoadAsync(project);
        Assert.Single(loaded.Documents);
        Assert.Equal("View.axaml", Assert.Single(loaded.Compiler.ProjectDocuments).LogicalPath);
        Assert.True(Assert.Single(loaded.Analyze()).Output.Success);
    }

    [Fact]
    public async Task EmptyLogicalMetadataFallsBackToProjectRelativePhysicalPath()
    {
        using var workspace = new AdhocWorkspace();
        var root = Path.GetFullPath("Project");
        var path = Path.Combine(root, "Views", "View.axaml");
        var project = Create(workspace, root).AddAdditionalDocument("View.axaml", SourceText.From(Source), filePath: path).Project;
        project = Configure(project, root, path, "build_metadata.AdditionalFiles.XamlGLogicalPath =");
        var loaded = await XamlWorkspaceProjectLoader.LoadAsync(project);
        Assert.Equal("Views/View.axaml", Assert.Single(loaded.Compiler.ProjectDocuments).LogicalPath);
    }

    private static Project Configure(Project project, string root, string path, string metadata) => project.AddAnalyzerConfigDocument(
        "Input.globalconfig", SourceText.From("is_global = true\n[" + path.Replace('\\', '/') + "]\n" + metadata + "\n"),
        filePath: Path.Combine(root, "Input.globalconfig")).Project;

    private static Project Create(AdhocWorkspace workspace, string root) => workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Inputs", "Inputs", LanguageNames.CSharp,
            filePath: Path.Combine(root, "Inputs.csproj")))
        .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
        .WithMetadataReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)))
        .AddDocument("Model.cs", "namespace Inputs { public class View { } }").Project;
}
