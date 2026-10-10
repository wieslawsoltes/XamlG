using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using XamlG.Roslyn;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceCatalogCacheTests
{
    [Fact]
    public void Catalog_equivalence_preserves_multiplicity_and_every_factory_field_without_display_keys()
    {
        var type = CompilationFactory.Create("public class Root { public static Root Build() => new(); }").GetTypeByMetadataName("Root")!;
        var a = new XamlResourceDescriptor("xamlg://test/A.xaml", type, "A.xaml", "Generated", null);
        // Equal old sort keys but different compared fields must not make order
        // significant; duplicate multiplicity must still invalidate the catalog.
        var b = a with { LocalFactoryMethod = "Other" };
        Assert.True(Equivalent(new(new[] { a, b, a }), new(new[] { b, a, a })));
        Assert.False(Equivalent(new(new[] { a, b, a }), new(new[] { a, b })));
        foreach (var different in new[] { a with { GeneratedNamespace = "Other" }, a with { LocalDocumentId = "Other.xaml" },
            a with { LocalFactoryType = type }, a with { ExternalFactory = type.GetMembers("Build").OfType<IMethodSymbol>().Single() }, b })
            Assert.False(Equivalent(new(new[] { a }), new(new[] { different })));
        var ambiguous = new XamlResourceCatalog(new[] { a, a });
        Assert.Contains("ambiguous", ambiguous.Resolve(null, a.Uri).Error!);
    }

    [Fact]
    public void External_export_descriptors_are_shared_only_within_the_type_system()
    {
        var producer = CompilationFactory.Create("""
            [assembly: XamlG.Runtime.XamlCompiledResourceAttribute("xamlg://producer/View.xaml", typeof(Factory), "Build")]
            public class Root { }
            public static class Factory { public static Root Build(System.IServiceProvider services) => new(); }
            """).WithAssemblyName("Producer").AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeSession).Assembly.Location));
        using var bytes = new MemoryStream();
        var emitted = producer.Emit(bytes);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var baseline = CompilationFactory.Create("public class Consumer { }");
        var compilation = baseline.AddReferences(MetadataReference.CreateFromImage(bytes.ToArray()));
        var types = new RoslynTypeSystem(compilation);
        XamlResourceCatalog Catalog(RoslynTypeSystem owner) => XamlResourceCatalogBuilder.Create(
            Array.Empty<XamlProjectDocument>(), owner, XamlFrameworkProfile.Portable, new());
        var first = Assert.Single(Catalog(types).Resources);
        Assert.Same(first, Assert.Single(Catalog(types).Resources));
        Assert.Empty(Catalog(new RoslynTypeSystem(baseline)).Resources);
        Assert.NotSame(first, Assert.Single(Catalog(new RoslynTypeSystem(compilation)).Resources));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => XamlResourceCatalogBuilder.Create(
            Array.Empty<XamlProjectDocument>(), types, XamlFrameworkProfile.Portable, new(), cancelled.Token));
    }

    [Fact]
    public void Custom_root_rules_observe_eager_runtime_diagnostics_as_before()
    {
        var observer = new ObserveDiagnostics();
        var profile = new XamlFrameworkProfile
        {
            Runtime = new() { Services = ImmutableArray.Create(new XamlServiceMapping("Missing.Contract", XamlServiceKind.RootObject)) },
            TypeBindingRules = ImmutableArray.Create<IXamlTypeBindingRule>(observer)
        };
        var types = new RoslynTypeSystem(CompilationFactory.Create("namespace Model { public class Item { } }"));
        var tree = XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:Model'/>");
        _ = XamlResourceCatalogBuilder.Create(new[] { new XamlProjectDocument(tree, "View.xaml") }, types, profile, new());
        Assert.True(observer.Observed);
    }

    private static bool Equivalent(XamlResourceCatalog a, XamlResourceCatalog b) => (bool)typeof(XamlProjectCompiler).Assembly
        .GetType("XamlG.CSharp.Resources.ResourceCatalogEquivalence")!.GetMethod("Equals", BindingFlags.Public | BindingFlags.Static)!
        .Invoke(null, new object[] { a, b })!;

    private sealed class ObserveDiagnostics : IXamlTypeBindingRule
    {
        public bool Observed { get; private set; }
        public bool TryResolve(BindingContext context, XamlTypeNameSyntax syntax, NamespaceScope scope, out INamedTypeSymbol? type)
        { Observed = context.Diagnostics.Any(d => d.Code == "XG1100"); type = null; return false; }
    }
}
