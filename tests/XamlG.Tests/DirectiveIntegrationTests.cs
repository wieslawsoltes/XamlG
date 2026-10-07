using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia;
using XamlG.Generator;
using Xunit;

namespace XamlG.Tests;

public sealed class DirectiveIntegrationTests
{
    private const string Ns = LoaderAdapterFixture.Namespace;
    private static XamlFrameworkProfile Profile()
    {
        var directives = new AvaloniaDirectivePolicy();
        return XamlFrameworkProfile.Portable with { Directives = directives, BindingRules = ImmutableArray.Create<IXamlBindingRule>(directives) };
    }

    [Theory]
    [InlineData("Loader.Load(this);")]
    [InlineData("Loader.Load((object)this);")]
    [InlineData("Loader.Load(null, this);")]
    public void SkippedComponentsRetainTheirOriginalInitializer(string statement)
    {
        var fixture = new LoaderAdapterFixture("namespace Model { public class Skipped : Root { public Skipped() { " + statement + " } } }", Profile(),
            ("Skipped.axaml", "<Missing " + Ns + " x:Class='Model.Skipped' x:Precompile='False'/>"),
            ("Kept.axaml", "<Root " + Ns + "/>"));
        Assert.Empty(fixture.Project.SourceIntegration.Source);
        var error = Assert.Throws<TargetInvocationException>(() => Activator.CreateInstance(fixture.Emit().GetType("Model.Skipped")!));
        Assert.Equal("Original loader reached", error.GetBaseException().Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DynamicObjectDispatchRetainsExplicitOptOuts(bool withServices)
    {
        var statement = withServices ? "Loader.Load(null, value);" : "Loader.Load(value);";
        var fixture = new LoaderAdapterFixture("namespace Model { public partial class Kept : Root { } public class Skipped : Kept { } public class Derived : Skipped { } public partial class IncludedDerived : Skipped { } " +
            "public static class Entry { public static void Populate(object value) { " + statement + " } } }", Profile(),
            ("Skipped.axaml", "<Missing " + Ns + " x:Class='Model.Skipped' x:Precompile='False'/>"),
            ("Kept.axaml", "<Root " + Ns + " x:Class='Model.Kept' Text='compiled'/>"),
            ("IncludedDerived.axaml", "<Root " + Ns + " x:Class='Model.IncludedDerived' Text='derived'/>") );
        var assembly = fixture.Emit();
        var populate = assembly.GetType("Model.Entry")!.GetMethod("Populate")!;
        var kept = Activator.CreateInstance(assembly.GetType("Model.Kept")!)!;
        populate.Invoke(null, new[] { kept });
        Assert.Equal("compiled", kept.GetType().GetProperty("Text")!.GetValue(kept));
        var includedDerived = Activator.CreateInstance(assembly.GetType("Model.IncludedDerived")!)!;
        populate.Invoke(null, new[] { includedDerived });
        Assert.Equal("derived", includedDerived.GetType().GetProperty("Text")!.GetValue(includedDerived));
        foreach (var name in new[] { "Model.Skipped", "Model.Derived" })
        {
            var skipped = Activator.CreateInstance(assembly.GetType(name)!);
            var error = Assert.Throws<TargetInvocationException>(() => populate.Invoke(null, new[] { skipped }));
            Assert.Equal("Original loader reached", error.GetBaseException().Message);
        }
        var unknown = Activator.CreateInstance(assembly.GetType("Model.Root")!);
        var unknownError = Assert.Throws<TargetInvocationException>(() => populate.Invoke(null, new[] { unknown }));
        Assert.IsType<InvalidOperationException>(unknownError.GetBaseException());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UriDispatchUsesOriginalLoaderOnlyForKnownOptedOutResources(bool withServices)
    {
        var call = withServices ? "Loader.Load(null, new System.Uri(path, System.UriKind.Relative), new System.Uri(baseUri))" :
            "Loader.Load(new System.Uri(path, System.UriKind.Relative), new System.Uri(baseUri))";
        var fixture = new LoaderAdapterFixture("namespace Model { public static class Entry { public static object Load(string path, string baseUri) => " + call + "; } }", Profile(),
            ("Resources/Skipped.axaml", "<Missing " + Ns + " x:Precompile='False'/>"),
            ("Resources/Kept.axaml", "<Root " + Ns + " Text='compiled'/>") );
        var method = fixture.Emit().GetType("Model.Entry")!.GetMethod("Load")!;
        var baseUri = "xamlg://" + fixture.Compilation.AssemblyName + "/Views/Main.axaml";
        var kept = method.Invoke(null, new object[] { "../Resources/Kept.axaml", baseUri })!;
        Assert.Equal("compiled", kept.GetType().GetProperty("Text")!.GetValue(kept));
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { "../Resources/Skipped.axaml", baseUri }));
        Assert.Equal("Original URI loader reached", error.GetBaseException().Message);
        var unknown = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { "../Resources/Unknown.axaml", baseUri }));
        Assert.IsType<InvalidOperationException>(unknown.GetBaseException());
    }

    [Theory]
    [InlineData("public static System.Action<Skipped> Call = Loader.Load;")]
    [InlineData("public static System.Linq.Expressions.Expression<System.Action<Skipped>> Call = value => Loader.Load(value);")]
    public void ExplicitlySkippedDelegateAndExpressionTreeTargetsRemainUntouched(string member)
    {
        var fixture = new LoaderAdapterFixture("namespace Model { public class Skipped : Root { } public static class Entry { " + member + " } }", Profile(),
            ("Skipped.axaml", "<Missing " + Ns + " x:Class='Model.Skipped' x:Precompile='False'/>"), ("Kept.axaml", "<Root " + Ns + "/>"));
        Assert.Empty(fixture.Project.SourceIntegration.Source);
        Assert.Empty(fixture.Project.SourceIntegration.Diagnostics);
        fixture.Emit();
    }

    [Fact]
    public void AnEntirelyOptedOutProjectDoesNotAdaptAnyLoaderCalls()
    {
        var fixture = new LoaderAdapterFixture("public static class Entry { public static System.Action<object> Call = Model.Loader.Load; }", Profile(),
            ("Skipped.axaml", "<Missing " + Ns + " x:Precompile='False'/>"));
        Assert.True(fixture.Project.Success);
        Assert.Empty(fixture.Project.SourceIntegration.Source);
        fixture.Emit();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratorHonorsOptOutBeforeRequiringFrameworkTypes(bool malformedXml)
    {
        var input = new InMemoryAdditionalText("Skipped.axaml", "<Missing xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Precompile='False'>" +
            (malformedXml ? "<Child>" : "<Child/>") + "</Missing>");
        var options = new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string> { ["build_property.XamlGFramework"] = "Avalonia" });
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new XamlIncrementalGenerator().AsSourceGenerator() }, new[] { input },
            new CSharpParseOptions(LanguageVersion.Preview), options);
        var result = driver.RunGenerators(CompilationFactory.Create("public class Unrelated { }")).GetRunResult();
        Assert.Empty(result.GeneratedTrees);
        Assert.Equal(malformedXml, result.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Id == "XG9000");
    }
}
