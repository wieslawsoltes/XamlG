using System.Collections;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ContentNormalizationExecutionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Fragmented_scalar_text_executes_in_standalone_and_shared_factories(bool shared, bool preserve)
    {
        var xaml = "<TextRoot xmlns='clr-namespace:WhitespaceFixture'" + (preserve ? " xml:space='preserve'" : "") +
            ">  a<!--gap--><![CDATA[  b  ]]>c  </TextRoot>";
        using var code = CompiledXaml.Create(xaml, ContentNormalizationTests.Model, shareAcrossDocuments: shared);
        var roots = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < 2; i++)
        {
            var root = code.Build();
            try
            {
                Assert.True(roots.Add(root));
                Assert.Equal(preserve ? "  a  b  c  " : "a b c", root.GetType().GetProperty("Text")!.GetValue(root));
            }
            finally { if (XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Significant_collections_keep_text_boundaries_and_fresh_child_instances(bool shared)
    {
        const string xaml = "<Root xmlns='clr-namespace:WhitespaceFixture'> first <Trim/> middle <Plain/> tail </Root>";
        using var code = CompiledXaml.Create(xaml, ContentNormalizationTests.Model, shareAcrossDocuments: shared);
        var objects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < 2; i++)
        {
            var root = code.Build();
            try
            {
                var children = (IList)root.GetType().GetProperty("Children")!.GetValue(root)!;
                Assert.Equal(5, children.Count);
                Assert.Equal("first", children[0]); Assert.Equal("middle ", children[2]); Assert.Equal(" tail", children[4]);
                Assert.Equal("Trim", children[1]!.GetType().Name); Assert.Equal("Plain", children[3]!.GetType().Name);
                Assert.True(objects.Add(children[1]!)); Assert.True(objects.Add(children[3]!));
            }
            finally { if (XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose(); }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Edits_preserve_source_metadata_and_match_fresh_project_output(int parallelism)
    {
        var compilation = CompilationFactory.Create(ContentNormalizationTests.Model)
            .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = parallelism };
        var compiler = new XamlProjectCompiler();
        const string before = "<TextRoot xmlns='clr-namespace:WhitespaceFixture'> one<!--c--><![CDATA[ two ]]></TextRoot>";
        var syntax = XamlSyntaxTree.Parse(before, "Text.xaml");
        var original = syntax.Text;
        foreach (var body in new[] { before, before.Replace("one", "three"), before.Replace("TextRoot xmlns", "TextRoot xml:space='preserve' xmlns"), before })
        {
            syntax = syntax.WithChanges(new[] { new XamlTextChange(new TextSpan(0, syntax.Text.Length), body) }, syntax.Version);
            var documents = new[] { new XamlProjectDocument(syntax, "Text.xaml"),
                new XamlProjectDocument(XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:WhitespaceFixture'> a <Trim/> b </Root>", "Root.xaml"), "Root.xaml") };
            var actual = compiler.Compile(documents, compilation, options: options);
            var fresh = new XamlProjectCompiler().Compile(documents, compilation, options: options);
            Assert.True(actual.Success, string.Join("\n", actual.Documents.SelectMany(d => d.Output.Diagnostics)));
            Assert.True(fresh.Success);
            Assert.Equal(Signature(fresh), Signature(actual));
            using var stream = new MemoryStream();
            var emitted = XamlCSharpCompilation.AddGeneratedSources(compilation, actual).Emit(stream);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        }
        Assert.Equal(before, original);
    }

    private static string Signature(XamlProjectCompilation project) => JsonSerializer.Serialize(project.Documents.Select(d =>
        new { d.Input.LogicalPath, d.Output.Source, d.Output.SourceMappings, d.Output.Diagnostics }));
}
