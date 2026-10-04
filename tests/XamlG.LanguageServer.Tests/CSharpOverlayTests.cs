using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Syntax;
using XamlG.Tooling;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class CSharpOverlayTests
{
    [Fact]
    public async Task UnsavedCSharpChangesXamlSemanticsAndClosingRestoresLoadedSource()
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: new[] { "MODEL" });
        var code = "#if MODEL\nnamespace Model { public class View { public string Text {get;set;} } }\n#endif";
        var tree = CSharpSyntaxTree.ParseText(code, parse, "/Model.cs");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Overlay", new[] { tree }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var compiler = new XamlCompilationSession(compilation);
        var store = new LspDocumentStore();
        var xaml = store.Open("file:///View.xaml", 1, "<View xmlns='clr-namespace:Model' Text='value'/>");
        await using var cache = new LspProjectAnalysisCache();
        Assert.True(Assert.Single(await cache.GetAsync(compiler, store.Capture())).Output.Success);
        store.OpenCSharp("file:///Model.cs", 4, code.Replace("string Text", "int Count"));
        var modified = await cache.GetWorkspaceAsync(compiler, store.Capture());
        Assert.Contains(Assert.Single(modified.Documents).Output.Diagnostics, d => d.Code == "XG1005");
        Assert.Same(parse, modified.Compiler.Types.Compilation.SyntaxTrees.Single().Options);
        Assert.True(store.IsCurrent(xaml));
        store.Close("file:///Model.cs");
        Assert.True(Assert.Single(await cache.GetAsync(compiler, store.Capture())).Output.Success);
        Assert.Same(tree, compiler.Types.Compilation.SyntaxTrees.Single());
    }
    [Fact]
    public void CSharpBuffersUseSequentialUtf16EditsAndShareOpenBudget()
    {
        var store = new LspDocumentStore(maximumDocuments: 1);
        store.OpenCSharp("file:///Model.cs", 1, "😀old");
        var updated = store.ChangeCSharp("file:///Model.cs", 2, new[]
        {
            new LspTextChange(new(new(0, 2), new(0, 5)), "new"),
            new LspTextChange(new(new(0, 0), new(0, 2)), "prefix ")
        });
        Assert.Equal("prefix new", updated.Text.ToString());
        Assert.Throws<LspRequestException>(() => store.Open("file:///View.xaml", 1, "<A/>"));
        Assert.Throws<LspRequestException>(() => store.ChangeCSharp(updated.Uri, 1, Array.Empty<LspTextChange>()));
        Assert.Empty(store.Snapshots); Assert.Single(store.Capture().CSharpDocuments);
    }
    [Fact]
    public void CSharpOverlaysCannotAliasAnOpenXamlBuffer()
    {
        var store = new LspDocumentStore(); store.Open("file:///same.cs", 1, "<A/>");
        Assert.Throws<LspRequestException>(() => store.OpenCSharp("file:///same.%63s", 1, "class A {}"));
    }
}
