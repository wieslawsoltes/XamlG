using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Generator;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class ProjectResourceGeneratorTests
{
    [Fact]
    public void PublicResourceFactoriesAreExportedAndOutputRemainsDeterministic()
    {
        var compilation = CompilationFactory.Create("namespace Example { public class Value { public string Text {get;set;} } }");
        if (!compilation.References.OfType<PortableExecutableReference>().Any(r => r.FilePath == typeof(XamlRuntimeContext).Assembly.Location))
            compilation = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        var inputs = new[] { new InMemoryAdditionalText("Resources/Value.axaml", "<Value xmlns='clr-namespace:Example' Text='linked'/>") };
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new XamlIncrementalGenerator().AsSourceGenerator() }, inputs,
            new CSharpParseOptions(LanguageVersion.Preview), new TestAnalyzerConfigOptionsProvider());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(driver.GetRunResult().GeneratedTrees).ToString();
        Assert.Contains("XamlCompiledResourceAttribute", generated);
        Assert.Contains("Resources/Value.axaml", generated);
        using var image = new MemoryStream(); var emitted = output.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        driver = driver.RunGenerators(compilation);
        Assert.Equal(generated, Assert.Single(driver.GetRunResult().GeneratedTrees).ToString());
    }
}
