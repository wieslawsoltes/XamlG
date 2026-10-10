using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using XamlG.Compiler.Resources;
using XamlG.Generator;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class GeneratorInputGateTests
{
    private const string Markup = "<Item xmlns='clr-namespace:Model' Text='hello'/>";
    private static CSharpCompilation Compilation() => CompilationFactory.Create("namespace Model { public class Item { public string Text {get;set;} } }")
        .AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
    private static GeneratorDriver Driver(AdditionalText file, TestAnalyzerConfigOptionsProvider options) =>
        CSharpGeneratorDriver.Create(new[] { new XamlIncrementalGenerator().AsSourceGenerator() }, new[] { file },
            new CSharpParseOptions(LanguageVersion.Preview), options);
    private static TestAnalyzerConfigOptionsProvider Options(bool global, string? file = null) => new(
        new Dictionary<string, string> { ["build_property.XamlGEnabled"] = global.ToString() },
        file == null ? null : new Dictionary<string, IReadOnlyDictionary<string, string>>
        { ["View.xaml"] = new Dictionary<string, string> { [XamlInputMetadata.CompileKey] = file } });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disabled_inputs_are_not_opened_and_reenabling_reads_the_current_snapshot(bool global)
    {
        var file = new CountingText("View.xaml", Markup);
        var compilation = Compilation();
        var disabled = global ? Options(false) : Options(true, "false");
        var driver = Driver(file, disabled).RunGenerators(compilation);
        Assert.Equal(0, file.Reads);
        Assert.Empty(driver.GetRunResult().GeneratedTrees);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        driver = driver.WithUpdatedAnalyzerConfigOptions(Options(true)).RunGenerators(compilation);
        Assert.Equal(1, file.Reads);
        Assert.Single(driver.GetRunResult().GeneratedTrees);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        driver = driver.WithUpdatedAnalyzerConfigOptions(disabled).RunGenerators(compilation);
        Assert.Equal(1, file.Reads);
        Assert.Empty(driver.GetRunResult().GeneratedTrees);
        var edited = new CountingText(file.Path, Markup.Replace("hello", "updated"));
        driver = driver.ReplaceAdditionalText(file, edited).RunGenerators(compilation);
        Assert.Equal(0, edited.Reads);
        driver = driver.WithUpdatedAnalyzerConfigOptions(Options(true)).RunGenerators(compilation);
        Assert.Equal(1, edited.Reads);
        Assert.Contains("updated", Assert.Single(driver.GetRunResult().GeneratedTrees).GetText().ToString());
    }

    [Fact]
    public void Invalid_boolean_flags_keep_the_existing_enabled_fallback()
    {
        var file = new CountingText("View.xaml", Markup);
        var options = new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string> { ["build_property.XamlGEnabled"] = "invalid" },
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { [file.Path] = new Dictionary<string, string> { [XamlInputMetadata.CompileKey] = "invalid" } });
        var result = Driver(file, options).RunGenerators(Compilation()).GetRunResult();
        Assert.Equal(1, file.Reads); Assert.Single(result.GeneratedTrees); Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Unsupported_files_and_disabled_invalid_frameworks_produce_no_input_reads()
    {
        var file = new CountingText("View.txt", "not XAML");
        Assert.Empty(Driver(file, Options(true)).RunGenerators(Compilation()).GetRunResult().GeneratedTrees);
        Assert.Equal(0, file.Reads);
        var xaml = new CountingText("View.xaml", "<");
        var options = new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string>
        { ["build_property.XamlGEnabled"] = "false", ["build_property.XamlGFramework"] = "MissingFramework" });
        var result = Driver(xaml, options).RunGenerators(Compilation()).GetRunResult();
        Assert.Empty(result.Diagnostics); Assert.Empty(result.GeneratedTrees); Assert.Equal(0, xaml.Reads);
    }

    private sealed class CountingText(string path, string content) : AdditionalText
    {
        private readonly SourceText _text = SourceText.From(content);
        public override string Path => path;
        public int Reads { get; private set; }
        public override SourceText GetText(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; return _text; }
    }
}
