using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceCatalogRemovalTests
{
    private static Microsoft.CodeAnalysis.CSharp.CSharpCompilation Compilation() =>
        CompilationFactory.Create("namespace Model { public class A { public string Text {get;set;} } }")
            .AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
    private static XamlProjectDocument Document(string path, string value) =>
        new(XamlSyntaxTree.Parse("<A xmlns='clr-namespace:Model' Text='" + value + "'/>", path), path);
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Removing_non_exported_documents_refreshes_shared_layout_without_rebinding_survivors(int concurrency)
    {
        var compilation = Compilation();
        var compiler = new XamlProjectCompiler();
        var options = new XamlCompilerOptions { GenerateBuildMethod = false, MaxDegreeOfParallelism = concurrency };
        var first = Document("First.xaml", "first");
        var second = Document("Second.xaml", "second");
        var initial = compiler.Compile(new[] { first, second }, compilation, options: options);
        Assert.True(initial.Success);
        Assert.Empty(initial.Resources.Resources);
        var removed = compiler.Compile(new[] { second }, compilation, options: options);
        var clean = new XamlProjectCompiler().Compile(new[] { second }, compilation, options: options);
        Assert.True(removed.Success);
        Assert.Equal(new XamlProjectStatistics(0, 1, 1, 0), removed.Statistics);
        Assert.Same(initial.Documents[1].Document, removed.Documents[0].Document);
        Assert.Equal(clean.Documents[0].Output.Source, removed.Documents[0].Output.Source);
        Assert.Equal(clean.Documents[0].Output.SourceMappings, removed.Documents[0].Output.SourceMappings);
        var restored = compiler.Compile(new[] { first, second }, compilation, options: options);
        Assert.Equal(initial.Documents.Select(d => d.Output.Source), restored.Documents.Select(d => d.Output.Source));
        Assert.Equal(new XamlProjectStatistics(0, 2, 0, 2), compiler.Compile(new[] { first, second }, compilation, options: options).Statistics);
    }

}
