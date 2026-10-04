using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ProjectIncrementalityTests
{
    private static CSharpCompilation Compilation()
    {
        var result = CompilationFactory.Create("namespace Model { public class A { public string Text {get;set;} } public class B {public string Text {get;set;} } }");
        if (!result.References.OfType<PortableExecutableReference>().Any(r => r.FilePath == typeof(XamlRuntimeContext).Assembly.Location))
            result = result.AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        return result;
    }
    private static XamlProjectDocument Document(string path, string text, string type = "A") =>
        new(XamlSyntaxTree.Parse("<" + type + " xmlns='clr-namespace:Model' Text='" + text + "'/>", path), path);

    [Fact]
    public void ValueOnlyEditBindsAndEmitsExactlyOneDocument()
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler();
        var first = Document("First.xaml", "first"); var second = Document("Second.xaml", "second");
        var initial = compiler.Compile(new[] { first, second }, compilation);
        Assert.True(initial.Success); Assert.Equal(new XamlProjectStatistics(2, 0, 2, 0), initial.Statistics);
        var unchanged = compiler.Compile(new[] { first, second }, compilation);
        Assert.Equal(new XamlProjectStatistics(0, 2, 0, 2), unchanged.Statistics);
        var changed = compiler.Compile(new[] { Document(first.LogicalPath, "changed"), second }, compilation);
        Assert.Equal(new XamlProjectStatistics(1, 1, 1, 1), changed.Statistics);
        Assert.Same(initial.Documents[1].Document, changed.Documents[1].Document);
        Assert.Same(initial.Documents[1].Output, changed.Documents[1].Output);
    }
    [Fact]
    public void ExportSignatureChangeInvalidatesDependentSymbolAssumptions()
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler();
        var second = Document("Second.xaml", "second");
        compiler.Compile(new[] { Document("First.xaml", "first"), second }, compilation);
        var changed = compiler.Compile(new[] { Document("First.xaml", "first", "B"), second }, compilation);
        Assert.Equal(2, changed.Statistics.BoundDocuments);
        Assert.Equal(0, changed.Statistics.ReusedBindings);
    }
    [Fact]
    public void NewRoslynCompilationAndExplicitClearReleaseCachedBindings()
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler(); var document = Document("A.xaml", "a");
        compiler.Compile(new[] { document }, compilation);
        var next = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("internal class Added { }"));
        Assert.Equal(1, compiler.Compile(new[] { document }, next).Statistics.BoundDocuments);
        compiler.ClearCache();
        Assert.Equal(1, compiler.Compile(new[] { document }, next).Statistics.BoundDocuments);
    }
    [Fact]
    public void CancellationDoesNotPublishAnIncompleteResult()
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler(); var document = Document("A.xaml", "a");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => compiler.Compile(new[] { document }, compilation, cancellationToken: cancellation.Token));
        Assert.True(compiler.Compile(new[] { document }, compilation).Success);
    }
}
