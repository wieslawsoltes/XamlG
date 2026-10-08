using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using XamlG.Compiler;
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
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ValueOnlyEditBindsAndEmitsExactlyOneDocument(int concurrency)
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler();
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = concurrency };
        var first = Document("First.xaml", "first"); var second = Document("Second.xaml", "second");
        var initial = compiler.Compile(new[] { first, second }, compilation, options: options);
        Assert.True(initial.Success); Assert.Equal(new XamlProjectStatistics(2, 0, 2, 0), initial.Statistics);
        var unchanged = compiler.Compile(new[] { first, second }, compilation, options: options);
        Assert.Equal(new XamlProjectStatistics(0, 2, 0, 2), unchanged.Statistics);
        var changed = compiler.Compile(new[] { Document(first.LogicalPath, "changed"), second }, compilation, options: options);
        Assert.Equal(new XamlProjectStatistics(1, 1, 1, 1), changed.Statistics);
        Assert.Same(initial.Documents[1].Document, changed.Documents[1].Document);
        Assert.Same(initial.Documents[1].Output, changed.Documents[1].Output);
    }
    [Fact]
    public void ExportSignatureChangeInvalidatesDependentSymbolAssumptions()
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler(); var second = Document("Second.xaml", "second");
        compiler.Compile(new[] { Document("First.xaml", "first"), second }, compilation);
        var changed = compiler.Compile(new[] { Document("First.xaml", "first", "B"), second }, compilation);
        Assert.Equal(2, changed.Statistics.BoundDocuments); Assert.Equal(0, changed.Statistics.ReusedBindings);
    }
    [Fact]
    public void NewRoslynCompilationAndExplicitClearReleaseCachedBindings()
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler(); var document = Document("A.xaml", "a");
        compiler.Compile(new[] { document }, compilation);
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
        var next = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("internal class Added { }", parseOptions));
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
    [Fact]
    public void ConcurrentDocumentsHaveIdenticalOutputDiagnosticsAndCacheBehavior()
    {
        var compilation = Compilation();
        var inputs = Enumerable.Range(0, 32).Select(i => Document(i + ".xaml", "value " + i)).ToArray();
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = 4 };
        var serial = new XamlProjectCompiler().Compile(inputs, compilation);
        var compiler = new XamlProjectCompiler();
        var parallel = compiler.Compile(inputs.Reverse(), compilation, options: options);
        Assert.True(serial.Success); Assert.True(parallel.Success);
        Assert.Equal(serial.Statistics, parallel.Statistics);
        Assert.Equal(serial.Documents.Select(document => document.Output.Source), parallel.Documents.Select(document => document.Output.Source));
        Assert.Equal(serial.Documents.SelectMany(document => document.Output.Diagnostics), parallel.Documents.SelectMany(document => document.Output.Diagnostics));
        Assert.Equal(serial.SourceIntegration.Source, parallel.SourceIntegration.Source);
        var cached = compiler.Compile(inputs, compilation, options: options);
        Assert.Equal(new XamlProjectStatistics(0, 32, 0, 32), cached.Statistics);
        var duplicates = inputs.Concat(new[] { inputs[0] }).ToArray();
        serial = new XamlProjectCompiler().Compile(duplicates, compilation);
        parallel = compiler.Compile(duplicates, compilation, options: options);
        Assert.False(serial.Success); Assert.False(parallel.Success);
        Assert.Equal(serial.Documents.SelectMany(document => document.Output.Diagnostics), parallel.Documents.SelectMany(document => document.Output.Diagnostics));
    }
}
