using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using Xunit;

namespace XamlG.Tests;

public sealed class CompilationCompositionTests
{
    [Fact]
    public void MultipleTreesAreNormalizedAtomicallyAndTheApplicationIsUnchanged()
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var first = CSharpSyntaxTree.ParseText("public class First { }", options, "First.cs");
        var second = CSharpSyntaxTree.ParseText("public class Second { }", options, "Second.cs");
        var application = Create(first, second);
        var result = XamlCSharpCompilation.AddGeneratedSources(application, GeneratedProject());
        Assert.Equal(3, result.SyntaxTrees.Count());
        Assert.All(result.SyntaxTrees, tree => Assert.Contains(XamlCSharpCompilation.InterceptorNamespace,
            ((CSharpParseOptions)tree.Options).Features["InterceptorsNamespaces"].Split(';')));
        Assert.Same(first, application.SyntaxTrees.First());
        Assert.False(first.Options.Features.ContainsKey("InterceptorsNamespaces"));
        using var image = new MemoryStream();
        var emitted = result.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
    }
    [Fact]
    public void AlreadyConfiguredTreesRetainTheirIdentity()
    {
        var options = XamlCSharpCompilation.GeneratedParseOptions(new CSharpParseOptions(LanguageVersion.Preview));
        var tree = CSharpSyntaxTree.ParseText("public class A { }", options, "A.cs");
        var result = XamlCSharpCompilation.AddGeneratedSources(Create(tree), GeneratedProject());
        Assert.Same(tree, result.SyntaxTrees.First());
    }
    [Fact]
    public void PerTreePreprocessorDocumentationAndPathArePreserved()
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose, preprocessorSymbols: new[] { "ENABLED" });
        var tree = CSharpSyntaxTree.ParseText("#if ENABLED\npublic class Enabled {}\n#else\npublic class Disabled {}\n#endif", options, "Conditional.cs");
        var result = XamlCSharpCompilation.AddGeneratedSources(Create(tree), GeneratedProject());
        var replaced = result.SyntaxTrees.First();
        Assert.Equal(tree.ToString(), replaced.ToString());
        Assert.Equal(tree.FilePath, replaced.FilePath);
        Assert.Equal(options.DocumentationMode, replaced.Options.DocumentationMode);
        Assert.Equal(options.PreprocessorSymbolNames.ToArray(), ((CSharpParseOptions)replaced.Options).PreprocessorSymbolNames.ToArray());
        Assert.NotNull(result.GetTypeByMetadataName("Enabled"));
        Assert.Null(result.GetTypeByMetadataName("Disabled"));
    }
    [Fact]
    public void TreeIdentityBasedAnalyzerConfigurationSurvivesComposition()
    {
        var tree = CSharpSyntaxTree.ParseText("public class A { }", new CSharpParseOptions(LanguageVersion.Preview), "A.cs");
        var application = Create(tree);
        var provider = new IdentitySyntaxOptionsProvider(tree);
        application = application.WithOptions(application.Options.WithSyntaxTreeOptionsProvider(provider));
        var result = XamlCSharpCompilation.AddGeneratedSources(application, GeneratedProject());
        var replacement = result.SyntaxTrees.First();
        var forwarded = result.Options.SyntaxTreeOptionsProvider!;
        Assert.Equal(GeneratedKind.MarkedGenerated, forwarded.IsGenerated(replacement, default));
        Assert.True(forwarded.TryGetDiagnosticValue(replacement, "CS0169", default, out var severity));
        Assert.Equal(ReportDiagnostic.Error, severity);
        Assert.True(forwarded.TryGetGlobalDiagnosticValue("CS1591", default, out severity));
        Assert.Equal(ReportDiagnostic.Suppress, severity);
    }
    [Fact]
    public void UnrelatedFeatureMismatchesAreNotSilentlyOverridden()
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var application = Create(CSharpSyntaxTree.ParseText("class A {}", options));
        var other = options.WithFeatures(new Dictionary<string, string> { ["OtherFeature"] = "on" });
        Assert.Throws<ArgumentException>(() => XamlCSharpCompilation.AddGeneratedSources(application, GeneratedProject(), other));
    }
    [Fact]
    public void CancellationAndEmptyOutputLeaveTheInputSnapshotIntact()
    {
        var application = Create();
        Assert.Throws<OperationCanceledException>(() => XamlCSharpCompilation.AddGeneratedSources(application, GeneratedProject(), cancellationToken: new(true)));
        var empty = new XamlProjectCompilation(ImmutableArray<XamlProjectDocumentResult>.Empty, new XamlResourceCatalog(Array.Empty<XamlResourceDescriptor>()));
        Assert.Same(application, XamlCSharpCompilation.AddGeneratedSources(application, empty));
    }
    private static CSharpCompilation Create(params SyntaxTree[] trees) => CompilationFactory.Create(string.Empty).RemoveAllSyntaxTrees().AddSyntaxTrees(trees);
    private static XamlProjectCompilation GeneratedProject() => new(ImmutableArray<XamlProjectDocumentResult>.Empty,
        new XamlResourceCatalog(Array.Empty<XamlResourceDescriptor>()))
    { SourceIntegration = new("internal static class GeneratedMarker { }", ImmutableArray<Diagnostic>.Empty) };
}
