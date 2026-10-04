using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Generator;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class GeneratorLogicalPathTests
{
    [Theory]
    [InlineData("", "Resources/Value.axaml")]
    [InlineData("   ", "Resources/Value.axaml")]
    [InlineData("Linked/Value.axaml", "Linked/Value.axaml")]
    [InlineData("/project/Resources/Value.axaml", "Resources/Value.axaml")]
    public void EmptyMetadataFallsBackWithoutDiscardingExplicitLinks(string configured, string expected)
    {
        const string path = "/project/Resources/Value.axaml";
        var compilation = CompilationFactory.Create("namespace Example { public class Value { public string Text {get;set;} } }");
        if (!compilation.References.OfType<PortableExecutableReference>().Any(r => r.FilePath == typeof(XamlRuntimeContext).Assembly.Location))
            compilation = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        var options = new TestAnalyzerConfigOptionsProvider(
            new Dictionary<string, string> { ["build_property.MSBuildProjectDirectory"] = "/project" },
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [path] = new Dictionary<string, string> { ["build_metadata.AdditionalFiles.XamlGLogicalPath"] = configured }
            });
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new XamlIncrementalGenerator().AsSourceGenerator() },
            new[] { new InMemoryAdditionalText(path, "<Value xmlns='clr-namespace:Example' Text='ok'/>") },
            new CSharpParseOptions(LanguageVersion.Preview), options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var source = Assert.Single(driver.GetRunResult().GeneratedTrees).ToString();
        Assert.Contains("/" + expected + "\"", source);
        Assert.DoesNotContain("/project/", source);
        using var image = new MemoryStream();
        var emitted = output.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
    }
}
