using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class ProjectSessionTests
{
    private static XamlCompilationSession Create(params XamlProjectDocument[] documents)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ProjectSession", new[] { CSharpSyntaxTree.ParseText("namespace Model { public class Item { public string Text {get;set;} public object Child {get;set;} } public class Include { } }") }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new(compilation, XamlFrameworkProfile.Portable with { ObjectExpressionRules = ImmutableArray.Create<IXamlObjectExpressionRule>(new ProjectSessionResourceRule()) }, projectDocuments: documents);
    }
    [Fact]
    public void ProjectAndSingleDocumentHostsUseTheSameResourceCatalog()
    {
        var first = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'><Item.Child><Include Source='Second.xaml'/></Item.Child></Item>", "/workspace/First.xaml");
        var second = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model' Text='resource'/>", "/workspace/Second.xaml");
        var session = Create(new(first, "First.xaml"), new(second, "Second.xaml"));
        var single = session.Analyze(first);
        var batch = session.AnalyzeProject(new[] { first, second });
        Assert.True(single.Output.Success, string.Join("\n", single.Output.Diagnostics));
        Assert.Equal(single.Output.Source, batch.Single(a => ReferenceEquals(a.Syntax, first)).Output.Source);
        Assert.Contains(".Build(global::XamlG.Runtime.XamlResourceServices.Enter", single.Output.Source);
        Assert.Same(single, session.Analyze(first));
    }
    [Fact]
    public void AnUnsavedOverlayGetsCycleDiagnosticsWithoutMutatingTheBaseline()
    {
        var first = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'><Item.Child><Include Source='Second.xaml'/></Item.Child></Item>", "First.xaml");
        var second = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'/>", "Second.xaml");
        var session = Create(new(first, first.Path), new(second, second.Path));
        var edited = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'><Item.Child><Include Source='First.xaml'/></Item.Child></Item>", second.Path, version: 1);
        var analysis = session.Analyze(edited);
        Assert.Contains(analysis.Output.Diagnostics, d => d.Code == "XG3304");
        Assert.True(session.Analyze(first).Output.Success);
        Assert.True(session.Analyze(second).Output.Success);
    }
}
