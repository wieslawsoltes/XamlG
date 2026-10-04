using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class ProjectOverlayTests
{
    [Fact]
    public void SimultaneousOpenEditsUseOneResourceGraphAndClosingRestoresTheBaseline()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Overlays", new[] { CSharpSyntaxTree.ParseText("namespace Model { public class Item { public object Child {get;set;} } public class Include { } }") },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var profile = XamlFrameworkProfile.Portable with { ObjectExpressionRules = ImmutableArray.Create<IXamlObjectExpressionRule>(new ProjectSessionResourceRule()) };
        var a = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'/>", "A.xaml");
        var b = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'/>", "B.xaml");
        var session = new XamlCompilationSession(compilation, profile, projectDocuments: new[] { new XamlProjectDocument(a, a.Path), new XamlProjectDocument(b, b.Path) });
        var openA = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'><Item.Child><Include Source='B.xaml'/></Item.Child></Item>", a.Path, version: 1);
        var openB = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'><Item.Child><Include Source='A.xaml'/></Item.Child></Item>", b.Path, version: 1);
        Assert.True(session.Analyze(openA).Output.Success);
        var both = session.AnalyzeOverlays(new[] { openA, openB });
        Assert.All(both, result => Assert.Contains(result.Output.Diagnostics, d => d.Code == "XG3304"));
        Assert.True(Assert.Single(session.AnalyzeOverlays(new[] { openA })).Output.Success);
        Assert.True(session.Analyze(a).Output.Success);
    }
}
