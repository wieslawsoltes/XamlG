using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class LoaderCandidateCacheTests
{
    [Theory]
    [InlineData("Model.Loader.Load(this);")]
    [InlineData("Model.Loader.@Load(this);")]
    [InlineData("Model.Loader.L\\u006fad(this);")]
    public void Cached_candidates_keep_escaped_identifiers_and_exact_interception_locations(string statement)
    {
        var fixture = new LoaderAdapterFixture("namespace Model { public partial class View : Root { public View() { " + statement + " } } }",
            ("View.xaml", "<Root " + LoaderAdapterFixture.Namespace + " x:Class='Model.View'/>") );
        var compiler = new XamlLoaderAdapterCompiler();
        var first = compiler.Compile(fixture.Compilation, fixture.Project, fixture.Profile.SourceLoader);
        var second = compiler.Compile(fixture.Compilation, fixture.Project, fixture.Profile.SourceLoader);
        Assert.NotEmpty(first.Source);
        Assert.Equal(first.Source, second.Source);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    [Fact]
    public void Syntax_cache_does_not_reuse_semantics_after_a_different_tree_changes_binding()
    {
        var fixture = new LoaderAdapterFixture("namespace Other { public static class Loader { public static void Load(object value) {} } }",
            ("Value.xaml", "<Root " + LoaderAdapterFixture.Namespace + "/>") );
        var options = (CSharpParseOptions)fixture.Compilation.SyntaxTrees.First().Options;
        var call = CSharpSyntaxTree.ParseText("public class Entry { public void Run() => L.Load(this); }", options, "Call.cs");
        var alias = CSharpSyntaxTree.ParseText("global using L = Other.Loader;", options, "Alias.cs");
        var before = fixture.Compilation.AddSyntaxTrees(alias, call);
        var compiler = new XamlLoaderAdapterCompiler();
        Assert.Empty(compiler.Compile(before, fixture.Project, fixture.Profile.SourceLoader).Source);
        var after = before.ReplaceSyntaxTree(alias, CSharpSyntaxTree.ParseText("global using L = Model.Loader;", options, "Alias.cs"));
        var result = compiler.Compile(after, fixture.Project, fixture.Profile.SourceLoader);
        Assert.NotEmpty(result.Source);
        Assert.Equal(new XamlLoaderAdapterCompiler().Compile(after, fixture.Project, fixture.Profile.SourceLoader).Source, result.Source);
    }

    [Fact]
    public void Replacing_the_method_name_cache_preserves_method_group_errors_and_nameof_exclusions()
    {
        var fixture = new LoaderAdapterFixture("public static class Entry { public static System.Action<object> Value = Model.Loader.Load; public const string Name = nameof(Model.Loader.Load); }",
            ("Value.xaml", "<Root " + LoaderAdapterFixture.Namespace + "/>") );
        var compiler = new XamlLoaderAdapterCompiler();
        var first = compiler.Compile(fixture.Compilation, fixture.Project, fixture.Profile.SourceLoader);
        Assert.Single(first.Diagnostics);
        Assert.Empty(compiler.Compile(fixture.Compilation, fixture.Project, new("Model.Loader", "Different")).Diagnostics);
        var again = compiler.Compile(fixture.Compilation, fixture.Project, fixture.Profile.SourceLoader);
        Assert.Equal(first.Diagnostics.Select(d => (d.Id, d.GetMessage(), d.Location.SourceSpan)),
            again.Diagnostics.Select(d => (d.Id, d.GetMessage(), d.Location.SourceSpan)));
    }

    [Fact]
    public void Warm_candidates_respect_cancellation_and_current_document_class_membership()
    {
        var fixture = new LoaderAdapterFixture("namespace Model { public partial class View : Root { public View() { Loader.Load(this); } } }",
            ("View.xaml", "<Root " + LoaderAdapterFixture.Namespace + " x:Class='Model.View'/>") );
        var compiler = new XamlLoaderAdapterCompiler();
        var initial = compiler.Compile(fixture.Compilation, fixture.Project, fixture.Profile.SourceLoader);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => compiler.Compile(fixture.Compilation, fixture.Project, fixture.Profile.SourceLoader, cancelled.Token));
        var empty = new XamlProjectCompiler().Compile(Array.Empty<XamlProjectDocument>(), fixture.Compilation, fixture.Profile);
        var next = compiler.Compile(fixture.Compilation, empty, fixture.Profile.SourceLoader);
        Assert.NotEqual(initial.Source, next.Source);
        Assert.Equal(new XamlLoaderAdapterCompiler().Compile(fixture.Compilation, empty, fixture.Profile.SourceLoader).Source, next.Source);
    }
}
