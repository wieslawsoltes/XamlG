using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using XamlG.Tooling.Navigation;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class ResourceNavigationTests
{
    [Fact]
    public void ReferencesUsePhysicalPathsWhileCompletionUsesRelativeResourceUris()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Navigation", new[] { CSharpSyntaxTree.ParseText("namespace Model { public class Item { public object Child {get;set;} } public class Include { } }") },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var view = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'><Item.Child><Include Source='../Resources/Values.xaml'/></Item.Child></Item>", "/workspace/Views/Main.xaml");
        var values = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'/>", "/workspace/Resources/Values.xaml");
        var profile = XamlFrameworkProfile.Portable with
        {
            ObjectExpressionRules = ImmutableArray.Create<IXamlObjectExpressionRule>(new ProjectSessionResourceRule()),
            ResourceSourceMembers = XamlFrameworkProfile.Portable.ResourceSourceMembers.Add("Model.Include", "Source")
        };
        var compiler = new XamlCompilationSession(compilation, profile, projectDocuments: new[]
        { new XamlProjectDocument(view, "Views/Main.xaml"), new XamlProjectDocument(values, "Resources/Values.xaml") });
        var analysis = compiler.Analyze(view); Assert.True(analysis.Output.Success);
        var language = new XamlResourceLanguageService(compiler);
        var link = Assert.Single(language.GetReferences(analysis));
        Assert.Equal(values.Path, link.TargetPath); Assert.False(link.IsExternal);
        Assert.Equal("xamlg://navigation/Resources/Values.xaml", link.ResourceUri);
        var item = Assert.Single(language.GetCompletions(analysis, view.Text.IndexOf("../Resources", StringComparison.Ordinal)));
        Assert.Equal("../Resources/Values.xaml", item.InsertText); Assert.Equal("File", item.Kind);
        var workspace = compiler.AnalyzeWorkspace(new[] { view });
        Assert.Equal(2, workspace.Length); // closed dependencies remain available for workspace symbols/definitions.
    }
}
