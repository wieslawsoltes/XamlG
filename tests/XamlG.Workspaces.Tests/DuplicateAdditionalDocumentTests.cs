using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class DuplicateAdditionalDocumentTests
{
    private const string Source = "<View xmlns='clr-namespace:DuplicateInputs' Text='same physical input'/>";

    [Fact]
    public async Task IdenticalFrameworkAndExplicitInputsAreOneCompiledDocument()
    {
        using var workspace = new AdhocWorkspace();
        var path = Path.GetFullPath("View.axaml");
        var project = Create(workspace)
            .AddAdditionalDocument("Explicit.axaml", SourceText.From(Source), filePath: path).Project
            .AddAdditionalDocument("Framework.axaml", SourceText.From(Source), filePath: path).Project;
        var loaded = await XamlWorkspaceProjectLoader.LoadAsync(project);
        Assert.Single(loaded.Documents);
        var analysis = Assert.Single(loaded.Analyze());
        Assert.True(analysis.Output.Success, string.Join("\n", analysis.Output.Diagnostics));
        Assert.Equal("DuplicateInputs.View", analysis.Document.Root!.Type.ToDisplayString());
    }

    [Fact]
    public async Task ConflictingBuffersForOnePhysicalInputAreRejected()
    {
        using var workspace = new AdhocWorkspace();
        var path = Path.GetFullPath("View.axaml");
        var project = Create(workspace)
            .AddAdditionalDocument("First.axaml", SourceText.From(Source), filePath: path).Project
            .AddAdditionalDocument("Second.axaml", SourceText.From(Source.Replace("same physical input", "different content")), filePath: path).Project;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => XamlWorkspaceProjectLoader.LoadAsync(project));
        Assert.Contains("conflicting XAML AdditionalDocuments", error.Message);
        Assert.Contains(path, error.Message);
    }

    private static Project Create(AdhocWorkspace workspace) => workspace.AddProject("DuplicateInputs", LanguageNames.CSharp)
        .WithFilePath(Path.GetFullPath("DuplicateInputs.csproj"))
        .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
        .WithMetadataReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)))
        .AddDocument("Model.cs", "namespace DuplicateInputs { public class View { public string Text {get;set;} } }").Project;
}
