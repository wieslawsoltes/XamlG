using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using XamlG.CSharp.Integration;
using XamlG.Generator;
using Xunit;

namespace XamlG.Tests;

public sealed class InterceptedLoaderAnalyzerTests
{
    private const string Source = """
        namespace Avalonia.Markup.Xaml
        {
            public static class AvaloniaXamlLoader
            {
                public static void Load(object instance) =>
                    throw new System.InvalidOperationException("Unadapted loader reached");
            }
        }
        namespace Model
        {
            public sealed class View
            {
                public int Writes;
                public void Initialize() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            }
        }
        """;

    [Fact]
    public async Task UnadaptedLoaderCallsRemainErrors()
    {
        var compilation = Create(Source);
        var diagnostics = await AnalyzeAsync(compilation);
        var error = Assert.Single(diagnostics);
        Assert.Equal("XG2001", error.Id);
        Assert.Equal("View.cs", error.Location.GetLineSpan().Path);
    }

    [Fact]
    public async Task UnrelatedGeneratedCodeDoesNotDisableTheGuard()
    {
        var compilation = Create(Source);
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "namespace XamlG.Generated.Loaders { internal class Unrelated { } }",
            (CSharpParseOptions)compilation.SyntaxTrees.Single().Options,
            "Unrelated.g.cs"));
        Assert.Contains(await AnalyzeAsync(compilation), diagnostic => diagnostic.Id == "XG2001");
    }

    [Fact]
    public async Task ActuallyInterceptedCallsPassAnalysisAndExecuteTheReplacement()
    {
        var compilation = Create(Source);
        var original = compilation.SyntaxTrees.Single();
        var invocation = original.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var location = compilation.GetSemanticModel(original).GetInterceptableLocation(invocation)!;
        Assert.NotNull(location);
        var version = location.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var data = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(location.Data, true);
        var generated = """
            namespace System.Runtime.CompilerServices
            {
                [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
                file sealed class InterceptsLocationAttribute(int version, string data) : System.Attribute { }
            }
            namespace XamlG.Generated.Loaders
            {
                internal static class TestAdapter
                {
            """ + "\n[global::System.Runtime.CompilerServices.InterceptsLocation(" + version + ", " + data + ")]\n" + """
                    public static void Initialize(object instance) => ((global::Model.View)instance).Writes++;
                }
            }
            """;
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(generated,
            (CSharpParseOptions)original.Options, "Adapter.g.cs"));
        Assert.NotNull(compilation.GetSemanticModel(original).GetInterceptorMethod(invocation));
        var file = new CountingAdditionalText();
        Assert.Empty(await AnalyzeWithFileAsync(compilation, file));
        Assert.Equal(0, file.Reads);

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = Assembly.Load(image.ToArray());
        var type = assembly.GetType("Model.View", throwOnError: true)!;
        var view = Activator.CreateInstance(type)!;
        type.GetMethod("Initialize")!.Invoke(view, null);
        Assert.Equal(1, type.GetField("Writes")!.GetValue(view));
    }

    [Fact]
    public async Task GenuineGeneratorOptOutRetainsItsExistingBehavior()
    {
        var compilation = Create(Source);
        var options = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty,
            new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string>
            { ["build_property.XamlGEnabled"] = "false" }));
        var result = await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new AvaloniaLoaderMigrationAnalyzer()), options)
            .GetAnalyzerDiagnosticsAsync();
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PrecompileOptOutExemptsOnlyTheExcludedComponent(bool anotherDocument, bool sameClass)
    {
        const string ns = "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
        var files = ImmutableArray.CreateBuilder<AdditionalText>();
        files.Add(new InMemoryAdditionalText("Skipped.axaml", "<Missing " + ns + " x:Class='Model.View' x:Precompile='False'/>"));
        if (anotherDocument) files.Add(new InMemoryAdditionalText("Included.axaml", "<Missing " + ns + (sameClass ? " x:Class='Model.View'" : "") + "/>"));
        var options = new AnalyzerOptions(files.ToImmutable(), new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string>
            { ["build_property.XamlGFramework"] = "Avalonia" }));
        var diagnostics = await Create(Source).WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AvaloniaLoaderMigrationAnalyzer()), options).GetAnalyzerDiagnosticsAsync();
        Assert.Equal(sameClass ? 1 : 0, diagnostics.Length);
        if (sameClass) Assert.Equal("XG2001", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task OptOutResolvesNamedArgumentsByParameter()
    {
        var source = Source.Replace("Load(object instance)", "Load(System.IServiceProvider services, object instance)", StringComparison.Ordinal)
            .Replace("Load(this)", "Load(instance: this, services: null)", StringComparison.Ordinal);
        var files = ImmutableArray.Create<AdditionalText>(
            new InMemoryAdditionalText("Skipped.axaml", "<Missing xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Model.View' x:Precompile='False'/>"),
            new InMemoryAdditionalText("Included.axaml", "<Missing/>"));
        var options = new AnalyzerOptions(files, new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string> { ["build_property.XamlGFramework"] = "Avalonia" }));
        var diagnostics = await Create(source).WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AvaloniaLoaderMigrationAnalyzer()), options).GetAnalyzerDiagnosticsAsync();
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task APartialOptOutDoesNotExemptUnrelatedLoaderCalls()
    {
        var source = Source + "\npublic class Other { public void Initialize() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this); }";
        var files = ImmutableArray.Create<AdditionalText>(
            new InMemoryAdditionalText("Skipped.axaml", "<Missing xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Model.View' x:Precompile='False'/>"),
            new InMemoryAdditionalText("Included.axaml", "<Missing/>"));
        var options = new AnalyzerOptions(files, new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string> { ["build_property.XamlGFramework"] = "Avalonia" }));
        var diagnostics = await Create(source).WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AvaloniaLoaderMigrationAnalyzer()), options).GetAnalyzerDiagnosticsAsync();
        Assert.Equal("XG2001", Assert.Single(diagnostics).Id);
        Assert.True(diagnostics[0].Location.SourceSpan.Start > Source.Length);
    }

    [Fact]
    public async Task UnrelatedInvocationsDoNotReadXamlInputs()
    {
        var source = Source.Replace("Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this)", "System.GC.KeepAlive(this)", StringComparison.Ordinal);
        var file = new CountingAdditionalText();
        Assert.Empty(await AnalyzeWithFileAsync(Create(source), file));
        Assert.Equal(0, file.Reads);
    }

    [Fact]
    public async Task UnadaptedCallsShareOneOptOutScan()
    {
        var source = Source + string.Concat(Enumerable.Range(0, 20).Select(index =>
            "\nclass Other" + index + " { void Initialize() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.@Load(this); }"));
        var file = new CountingAdditionalText();
        var diagnostics = await AnalyzeWithFileAsync(Create(source), file);
        Assert.Equal(21, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("XG2001", diagnostic.Id));
        Assert.Equal(1, file.Reads);
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeWithFileAsync(CSharpCompilation compilation, AdditionalText file) =>
        compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AvaloniaLoaderMigrationAnalyzer()),
            new AnalyzerOptions(ImmutableArray.Create(file), new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string>
                { ["build_property.XamlGFramework"] = "Avalonia" }))).GetAnalyzerDiagnosticsAsync();

    private sealed class CountingAdditionalText : AdditionalText
    {
        public override string Path => "View.axaml";
        public int Reads;
        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return SourceText.From("<Missing/>");
        }
    }

    private static CSharpCompilation Create(string source)
    {
        var options = XamlCSharpCompilation.GeneratedParseOptions(new CSharpParseOptions(LanguageVersion.Preview));
        var tree = CSharpSyntaxTree.ParseText(source, options, "View.cs");
        return CompilationFactory.Create(string.Empty).RemoveAllSyntaxTrees().AddSyntaxTrees(tree);
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(CSharpCompilation compilation) =>
        compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AvaloniaLoaderMigrationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
}
