using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Integration;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class LoaderAdapterTests
{
    private const string Ns = LoaderAdapterFixture.Namespace;
    [Theory]
    [InlineData("Model.Loader.Load(this);")]
    [InlineData("L.Load((object)this);")]
    [InlineData("Load(this);")]
    public void HandwrittenInitializationUsesTheSameIdempotentFactory(string statement)
    {
        var fixture = new LoaderAdapterFixture("using L=Model.Loader; using static Model.Loader; namespace Model { public partial class View : Root { public View() { InitializeComponent(); InitializeComponent(); } private void InitializeComponent() { " + statement + " } } }",
            ("View.axaml", "<Root " + Ns + " x:Class='Model.View' Text='compiled'/>") );
        var root = fixture.Build("View.axaml");
        Assert.Equal("compiled", root.GetType().GetProperty("Text")!.GetValue(root));
        Assert.Equal(1, root.GetType().GetField("Writes")!.GetValue(root));
        Assert.Contains("InterceptsLocationAttribute", fixture.Project.SourceIntegration.Source);
        Assert.Contains("Original loader reached", fixture.Compilation.SyntaxTrees.Single().ToString());
    }
    [Fact]
    public void ServiceProviderSurvivesConstructorAndMarkupExtensionCalls()
    {
        var fixture = new LoaderAdapterFixture("namespace Model { public partial class View : Root { public View(System.IServiceProvider services) { Loader.Load(instance: this, services: services); } } }",
            ("View.axaml", "<Root " + Ns + " x:Class='Model.View' Value='{Service}'/>") );
        var root = fixture.Build("View.axaml", new StringServices());
        Assert.Equal("caller service", root.GetType().GetProperty("Value")!.GetValue(root));
    }
    [Fact]
    public void DynamicObjectSourceUsesAnExplicitGeneratedTypeDispatcher()
    {
        var fixture = new LoaderAdapterFixture("namespace Model { public partial class View : Root { public View() { Entry.Populate(this); } } public static class Entry { public static void Populate(object value) { Loader.Load(value); } } }",
            ("View.axaml", "<Root " + Ns + " x:Class='Model.View' Text='dynamic'/>") );
        var root = fixture.Build("View.axaml");
        Assert.Equal("dynamic", root.GetType().GetProperty("Text")!.GetValue(root));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UriOverloadsLoadCompiledFactoriesAndKeepBaseUriSemantics(bool withServices)
    {
        var call = withServices ? "Loader.Load(null, new System.Uri(relative, System.UriKind.Relative), new System.Uri(baseUri))" :
            "Loader.Load(new System.Uri(relative, System.UriKind.Relative), new System.Uri(baseUri))";
        var fixture = new LoaderAdapterFixture("namespace Model { public static class Entry { public static object Run(string relative, string baseUri) => " + call + "; } }",
            ("Resources/Value.xaml", "<Root " + Ns + " Text='uri result'/>") );
        var assembly = fixture.Emit();
        var result = assembly.GetType("Model.Entry")!.GetMethod("Run")!.Invoke(null,
            new object[] { "../Resources/Value.xaml", "xamlg://" + fixture.Compilation.AssemblyName + "/Views/Main.xaml" })!;
        Assert.Equal("uri result", result.GetType().GetProperty("Text")!.GetValue(result));
    }
    [Fact]
    public void UnrelatedLoaderSymbolsAreNotIntercepted()
    {
        var fixture = new LoaderAdapterFixture("namespace Other { public static class Loader { public static void Load(object value) {} } public class Use { public void Run() => Loader.Load(this); } }",
            ("Value.xaml", "<Root " + Ns + "/>") );
        Assert.Empty(fixture.Project.SourceIntegration.Source);
        Assert.Empty(fixture.Project.SourceIntegration.Diagnostics);
    }
    [Theory]
    [InlineData("public static System.Action<object> Value = Model.Loader.Load;")]
    [InlineData("public static System.Linq.Expressions.Expression<System.Action<object>> Value = x => Model.Loader.Load(x);")]
    public void UnsafeUninterceptableShapesHaveOriginalCSharpLocations(string member)
    {
        var fixture = new LoaderAdapterFixture("public static class Entry { " + member + " }", ("Value.xaml", "<Root " + Ns + "/>"));
        var diagnostic = Assert.Single(fixture.Project.SourceIntegration.Diagnostics);
        Assert.Equal("XG3400", diagnostic.Id); Assert.Equal("Code.cs", diagnostic.Location.GetLineSpan().Path);
        Assert.False(fixture.Project.Success);
    }
    [Fact]
    public void NameofIsNotAnExecutableLoaderCall()
    {
        var fixture = new LoaderAdapterFixture("public static class Entry { public const string Name = nameof(Model.Loader.Load); }", ("Value.xaml", "<Root " + Ns + "/>"));
        Assert.True(fixture.Project.Success); Assert.Empty(fixture.Project.SourceIntegration.Source);
    }
    [Fact]
    public void GeneratedParseOptionsPreserveOtherCompilerFeaturesAndSymbols()
    {
        var original = new CSharpParseOptions(LanguageVersion.CSharp13, preprocessorSymbols: new[] { "KEEP" })
            .WithFeatures(new Dictionary<string, string> { ["InterceptorsNamespaces"] = "Other.Generated", ["custom-feature"] = "keep" });
        var updated = XamlCSharpCompilation.GeneratedParseOptions(original);
        Assert.Equal(original.LanguageVersion, updated.LanguageVersion);
        Assert.Equal(original.PreprocessorSymbolNames, updated.PreprocessorSymbolNames);
        Assert.Equal("keep", updated.Features["custom-feature"]);
        Assert.Equal("Other.Generated;XamlG.Generated.Loaders", updated.Features["InterceptorsNamespaces"]);
        Assert.Equal("Other.Generated", original.Features["InterceptorsNamespaces"]);
    }
    private sealed class StringServices : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(string) ? "caller service" : null;
    }
}
