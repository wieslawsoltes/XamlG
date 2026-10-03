using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task AdhocWorkspaceUsesProjectTypesAndAdditionalDocuments()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Application", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)))
            .AddDocument("Model.cs", "namespace Demo { public class View { public string Text {get;set;} } }").Project
            .AddAdditionalDocument("View.xaml", SourceText.From("<View xmlns='clr-namespace:Demo' Text='hello'/>"), filePath: "View.xaml").Project;
        var snapshot = await XamlWorkspaceProjectLoader.LoadAsync(project);
        var analysis = Assert.Single(snapshot.Analyze());
        Assert.True(analysis.Output.Success, string.Join("\n", analysis.Output.Diagnostics));
        Assert.Equal("Demo.View", analysis.Document.Root!.Type.ToDisplayString());
        Assert.Equal("Application", snapshot.Compiler.Types.Compilation.AssemblyName);
        var updated = snapshot.WithDocumentText("View.xaml", "<View xmlns='clr-namespace:Demo' Text='updated'/>");
        Assert.NotEqual(snapshot.Documents["View.xaml"].Text, updated.Documents["View.xaml"].Text);
        Assert.Same(snapshot.Compiler, updated.Compiler);
    }

    [Fact]
    public void MSBuildEvaluationRequiresExplicitTrust()
    {
        Assert.Throws<InvalidOperationException>(() => XamlWorkspaceHost.Create(new XamlWorkspaceOptions()));
    }
}
