using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ProjectFinalizationIntegrationTests
{
    private const string Model = """
        namespace FinalizationModel {
            public class Item { public int Value { get; set; } }
            public partial class Window : Item { }
        }
        """;

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public void Edits_removals_and_restoration_match_fresh_outputs_and_compile(int concurrency, bool export)
    {
        var compilation = CompilationFactory.Create(Model)
            .AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = concurrency, GenerateBuildMethod = export };
        var compiler = new XamlProjectCompiler();
        var first = Document("First.xaml", 1);
        var second = Document("Second.xaml", 2);
        var third = Document("Third.xaml", 3);
        var initial = compiler.Compile([third, first, second], compilation, options: options);
        Assert.True(initial.Success);
        var warm = compiler.Compile([second, third, first], compilation, options: options);
        Assert.Equal(new XamlProjectStatistics(0, 3, 0, 3), warm.Statistics);
        for (var i = 0; i < 3; i++) Assert.Same(initial.Documents[i].Output, warm.Documents[i].Output);
        var edited = Document("Second.xaml", 22);
        foreach (XamlProjectDocument[] phase in new[]
        {
            new[] { second, third }, new[] { third }, new[] { first, second, third },
            new[] { first, edited, third }, Array.Empty<XamlProjectDocument>(), new[] { first, second, third }
        })
        {
            var actual = compiler.Compile(phase, compilation, options: options);
            var expected = new XamlProjectCompiler().Compile(phase, compilation, options: options);
            AssertEquivalent(expected, actual);
            Assert.True(actual.Success);
            var trees = actual.Documents.Select(d => CSharpSyntaxTree.ParseText(d.Output.Source, new CSharpParseOptions(LanguageVersion.Preview)));
            using var stream = new MemoryStream();
            var emitted = compilation.AddSyntaxTrees(trees).Emit(stream);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Duplicate_paths_addresses_and_classes_recover_without_stale_diagnostics(int concurrency)
    {
        var compilation = CompilationFactory.Create(Model)
            .AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        var options = new XamlCompilerOptions { GenerateBuildMethod = false, MaxDegreeOfParallelism = concurrency };
        var compiler = new XamlProjectCompiler();
        var first = Document("First.xaml", 1, className: true);
        var second = Document("Second.xaml", 2, className: true);
        var normal = Document("Third.xaml", 3);
        foreach (var inputs in new[]
        {
            new[] { first, normal },
            new[] { first, second with { LogicalPath = first.LogicalPath }, normal },
            new[] { first with { ResourceUri = "avares://app/shared.xaml" }, second with { ResourceUri = "avares://app/shared.xaml" }, normal },
            new[] { first, second, normal },
            new[] { first, normal },
            new[] { normal }
        })
        {
            var actual = compiler.Compile(inputs, compilation, options: options);
            var expected = new XamlProjectCompiler().Compile(inputs, compilation, options: options);
            AssertEquivalent(expected, actual);
            foreach (var item in actual.Documents)
            {
                var codes = item.Document.Diagnostics.Select(d => d.Code).ToArray();
                var uri = Array.LastIndexOf(codes, "XG3300");
                var type = Array.IndexOf(codes, "XG2002");
                Assert.True(uri < 0 || type < 0 || uri < type);
            }
        }
    }

    private static XamlProjectDocument Document(string path, int value, bool className = false) => new(
        XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:FinalizationModel' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
            (className ? "x:Class='FinalizationModel.Window' " : "") + "Value='" + value + "'/>", path), path);

    private static void AssertEquivalent(XamlProjectCompilation expected, XamlProjectCompilation actual)
    {
        Assert.Equal(expected.Success, actual.Success);
        Assert.Equal(expected.Documents.Length, actual.Documents.Length);
        for (var i = 0; i < actual.Documents.Length; i++)
        {
            Assert.Equal(expected.Documents[i].Input.LogicalPath, actual.Documents[i].Input.LogicalPath);
            Assert.Equal(expected.Documents[i].Document.Diagnostics.ToArray(), actual.Documents[i].Document.Diagnostics.ToArray());
            Assert.Equal(expected.Documents[i].Output.Diagnostics.ToArray(), actual.Documents[i].Output.Diagnostics.ToArray());
            Assert.Equal(expected.Documents[i].Output.Source, actual.Documents[i].Output.Source);
            Assert.Equal(expected.Documents[i].Output.SourceMappings.Select(m => (m.GeneratedSpan, m.SourceSpan, m.SourcePath)),
                actual.Documents[i].Output.SourceMappings.Select(m => (m.GeneratedSpan, m.SourceSpan, m.SourcePath)));
        }
    }
}
