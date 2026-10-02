using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using XamlG.Generator;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class GeneratorTests
{
    private const string Model = """
        using System;
        namespace Example
        {
            public class Panel { public string Text { get; set; } public int Count { get; set; } }
            public partial class Screen : Panel { public Screen() { InitializeComponent(); } }
        }
        """;

    private const string Markup = "<Panel xmlns='clr-namespace:Example' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Example.Screen' Text='hello' Count='12'/>";
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static CSharpCompilation Compilation(string model = Model)
    {
        var compilation = CompilationFactory.Create(model);
        return compilation.References.OfType<PortableExecutableReference>().Any(r => r.FilePath == typeof(XamlRuntimeContext).Assembly.Location)
            ? compilation : compilation.AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
    }

    private static GeneratorDriver Driver(IEnumerable<AdditionalText> files, TestAnalyzerConfigOptionsProvider? options = null) =>
        CSharpGeneratorDriver.Create(
            generators: new[] { new XamlIncrementalGenerator().AsSourceGenerator() },
            additionalTexts: files,
            parseOptions: ParseOptions,
            optionsProvider: options ?? new(),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    [Fact]
    public void GeneratesInitializerIntoTheUsersActualCompilation()
    {
        var driver = Driver(new[] { new InMemoryAdditionalText("Screen.axaml", Markup) });
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(), out var output, out var diagnostics);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Single(driver.GetRunResult().GeneratedTrees);
        using var assembly = new MemoryStream();
        var emitted = output.Emit(assembly);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
    }

    [Fact]
    public void DiagnosticLocationsPointIntoTheXamlAdditionalFile()
    {
        var input = new InMemoryAdditionalText("/project/Screen.axaml", Markup.Replace("Count='12'", "Missing='12'"));
        var result = Driver(new[] { input }).RunGenerators(Compilation()).GetRunResult();
        var error = Assert.Single(result.Diagnostics.Where(d => d.Id == "XG1005"));
        Assert.Equal(input.Path, error.Location.GetLineSpan().Path);
        Assert.Equal("Missing", input.GetText().ToString(error.Location.SourceSpan));
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void UnrelatedCSharpEditsReuseParsedXaml()
    {
        var driver = Driver(new[] { new InMemoryAdditionalText("Screen.axaml", Markup) });
        var compilation = Compilation();
        driver = driver.RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("internal class Unrelated { }", ParseOptions)));
        var result = Assert.Single(driver.GetRunResult().Results);
        Assert.All(result.TrackedSteps["XamlG.Parse"].SelectMany(s => s.Outputs), output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
        Assert.All(result.TrackedSteps["XamlG.Emit"].SelectMany(s => s.Outputs), output => Assert.Equal(IncrementalStepRunReason.Unchanged, output.Reason));
    }

    [Fact]
    public void EditingOneDocumentDoesNotReparseOtherDocuments()
    {
        var first = new InMemoryAdditionalText("First.axaml", "<Panel xmlns='clr-namespace:Example' Text='first'/>");
        var second = new InMemoryAdditionalText("Second.axaml", "<Panel xmlns='clr-namespace:Example' Text='second'/>");
        var compilation = Compilation(Model.Replace("InitializeComponent();", string.Empty));
        var driver = Driver(new[] { first, second }).RunGenerators(compilation);
        driver = driver.ReplaceAdditionalText(first, new InMemoryAdditionalText(first.Path, first.GetText().ToString().Replace("first", "edited"))).RunGenerators(compilation);
        var reasons = Assert.Single(driver.GetRunResult().Results).TrackedSteps["XamlG.Parse"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.Contains(IncrementalStepRunReason.Cached, reasons);
        Assert.Contains(IncrementalStepRunReason.Modified, reasons);
        Assert.Equal(2, driver.GetRunResult().GeneratedTrees.Length);
    }

    [Fact]
    public void CSharpMemberChangesInvalidateSemanticBinding()
    {
        var driver = Driver(new[] { new InMemoryAdditionalText("Screen.axaml", Markup) }).RunGenerators(Compilation());
        driver = driver.RunGenerators(Compilation(Model.Replace("public int Count { get; set; }", string.Empty)));
        Assert.Contains(driver.GetRunResult().Diagnostics, d => d.Id == "XG1005");
    }

    [Fact]
    public void ExplicitDisableProducesNoSourcesOrDiagnostics()
    {
        var options = new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string> { ["build_property.XamlGEnabled"] = "false" });
        var result = Driver(new[] { new InMemoryAdditionalText("Invalid.axaml", "<") }, options).RunGenerators(Compilation()).GetRunResult();
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void DuplicateCodeBehindDeclarationsHaveDedicatedDiagnostic()
    {
        var result = Driver(new[] { new InMemoryAdditionalText("One.axaml", Markup), new InMemoryAdditionalText("Two.axaml", Markup) }).RunGenerators(Compilation()).GetRunResult();
        Assert.Equal(2, result.Diagnostics.Count(d => d.Id == "XG2002"));
    }

    [Fact]
    public async Task MigrationAnalyzerUsesMethodSymbolsRatherThanTextMatching()
    {
        const string source = """
            namespace Avalonia.Markup.Xaml { public static class AvaloniaXamlLoader { public static void Load(object value) { } } }
            namespace Example {
                class UserView { void InitializeComponent() { Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this); } }
                class Unrelated { public void Load(object value) { } public void Method() { Load(this); } }
            }
            """;
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new AvaloniaLoaderMigrationAnalyzer());
        var diagnostics = await Compilation(source).WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync();
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("XG2001", diagnostic.Id);
    }
}
