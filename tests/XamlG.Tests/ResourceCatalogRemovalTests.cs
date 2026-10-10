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
        Assert.Equal(clean.Documents[0].Output.SourceMappings.Select(m => (m.GeneratedSpan, m.SourceSpan, m.SourcePath)),
            removed.Documents[0].Output.SourceMappings.Select(m => (m.GeneratedSpan, m.SourceSpan, m.SourcePath)));
        var restored = compiler.Compile(new[] { first, second }, compilation, options: options);
        Assert.Equal(initial.Documents.Select(d => d.Output.Source), restored.Documents.Select(d => d.Output.Source));
        Assert.Equal(new XamlProjectStatistics(0, 2, 0, 2), compiler.Compile(new[] { first, second }, compilation, options: options).Statistics);
    }

    [Fact]
    public void Cancellation_after_removal_does_not_lose_pending_layout_invalidation()
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler();
        var policy = new CallbackPolicy();
        var profile = XamlFrameworkProfile.Portable with { Directives = policy };
        var options = new XamlCompilerOptions { GenerateBuildMethod = false };
        var first = Document("First.xaml", "first"); var second = Document("Second.xaml", "second");
        Assert.True(compiler.Compile(new[] { first, second }, compilation, profile, options).Success);
        using var cancelled = new CancellationTokenSource();
        // Non-exported catalog probes skip ShouldCompile. This callback therefore
        // runs in CompileCore, after obsolete document entries have been removed.
        policy.Callback = cancelled.Cancel;
        Assert.Throws<OperationCanceledException>(() => compiler.Compile(new[] { second }, compilation, profile, options, cancelled.Token));
        policy.Callback = null;
        var recovered = compiler.Compile(new[] { second }, compilation, profile, options);
        var clean = new XamlProjectCompiler().Compile(new[] { second }, compilation, profile, options);
        Assert.Equal(new XamlProjectStatistics(0, 1, 1, 0), recovered.Statistics);
        Assert.Equal(clean.Documents[0].Output.Source, recovered.Documents[0].Output.Source);
    }

    private sealed class CallbackPolicy : IXamlDirectivePolicy
    {
        public Action? Callback { get; set; }
        private static IXamlDirectivePolicy Default => XamlFrameworkProfile.Portable.Directives;
        public bool ShouldCompile(XamlSyntaxTree syntax, XamlCompilerOptions options)
        { Callback?.Invoke(); return Default.ShouldCompile(syntax, options); }
        public IEnumerable<XamlDiagnostic> GetSkippedDiagnostics(XamlSyntaxTree syntax, CancellationToken cancellationToken) => Default.GetSkippedDiagnostics(syntax, cancellationToken);
        public string BindClassModifier(BindingContext context, XamlAttributeSyntax? directive) => Default.BindClassModifier(context, directive);
        public string BindFieldModifier(BindingContext context, XamlAttributeSyntax? directive) => Default.BindFieldModifier(context, directive);
    }

}
