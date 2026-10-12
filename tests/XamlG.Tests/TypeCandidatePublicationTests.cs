using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Roslyn;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class TypeCandidatePublicationTests
{
    private const string LibrarySource = "namespace ResolutionFixture { public class Item {} public class Box<T> {} internal class Hidden {} " +
        "public class Outer { private class Private {} public class Public {} } }";

    [Theory]
    [InlineData("urn:single")]
    [InlineData("urn:multiple")]
    [InlineData("urn:overlap")]
    [InlineData("urn:missing")]
    [InlineData("using:ResolutionFixture")]
    [InlineData("clr-namespace:ResolutionFixture;assembly=LibraryZ")]
    [InlineData("clr-namespace:ResolutionFixture;assembly=LibraryZ, Version=1.0.0.0")]
    [InlineData("clr-namespace:ResolutionFixture;assembly=libraryz")]
    public void Empty_single_and_ambiguous_results_match_the_previous_algorithm(string ns)
    {
        var types = Fixture();
        foreach (var (name, arity) in new[] { ("Item", 0), ("Box", 1), ("Box", 0), ("Hidden", 0),
            ("Outer+Private", 0), ("Outer+Public", 0), ("Missing", 0) })
        {
            var expected = Previous(types, ns, name, arity);
            Equal(expected, types.Resolve(ns, name, arity));
            Equal(expected, types.Resolve(ns, name, arity));
        }
    }

    [Fact]
    public void Forwarders_and_repeated_prefix_runs_keep_one_destination_symbol()
    {
        var library = Reference("LibraryZ", LibrarySource);
        var facade = Reference("Facade", "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(ResolutionFixture.Item))]", library);
        var types = new RoslynTypeSystem(CompilationFactory.Create("").AddReferences(library, facade), new()
        {
            NamespaceMappings = [new("urn:forward", "ResolutionFixture", "Facade"),
                new("urn:forward", "Does.Not.Exist", "LibraryZ"), new("urn:forward", "ResolutionFixture", "LibraryZ"),
                new("urn:forward", "ResolutionFixture")]
        });
        var result = types.Resolve("urn:forward", "Item");
        Assert.Single(result.Candidates);
        Assert.Equal("LibraryZ", result.Type!.ContainingAssembly.Name);
        Equal(Previous(types, "urn:forward", "Item", 0), result);
    }

    [Fact]
    public void Concurrent_cold_discovery_keeps_ordinal_ambiguity_order()
    {
        var types = Fixture();
        var expected = Previous(types, "urn:multiple", "Item", 0);
        var resolve = Core(types);
        Parallel.For(0, 128, _ => Equal(expected, resolve("urn:multiple", "Item", 0)));
        Assert.Equal(new[] { "LibraryA", "LibraryZ" }, expected.Candidates.Select(c => c.ContainingAssembly.Name));
    }

    [Fact]
    public void Compilation_replacement_does_not_reuse_missing_or_ambiguous_results()
    {
        var old = Fixture();
        Assert.True(old.Resolve("using:ResolutionFixture", "Item").IsAmbiguous);
        Assert.Null(old.Resolve("using:ResolutionFixture", "Added").Type);
        var replacement = new RoslynTypeSystem(CompilationFactory.Create("namespace ResolutionFixture { public class Added {} }"));
        Assert.NotNull(replacement.Resolve("using:ResolutionFixture", "Added").Type);
        Assert.Null(replacement.Resolve("using:ResolutionFixture", "Item").Type);
        Assert.True(old.Resolve("using:ResolutionFixture", "Item").IsAmbiguous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Untargeted_namespaces_compile_and_execute_independent_factories(bool shared)
    {
        var model = "using System.Collections.Generic; using XamlG.Runtime; namespace CandidateRuntime { " +
            "public class Root { [Content] public List<object> Children {get;} = new(); } " +
            string.Concat(Enumerable.Range(0, 32).Select(i => "public class Item" + i + " { public string Text {get;set;} = \"\"; }")) + "}";
        var xaml = "<Root xmlns='using:CandidateRuntime'>" +
            string.Concat(Enumerable.Range(0, 32).Select(i => "<Item" + i + " Text='value" + i + "'/>")) + "</Root>";
        using var code = CompiledXaml.Create(xaml, model, shareAcrossDocuments: shared);
        var roots = new[] { code.Build(), code.Build() };
        try
        {
            Assert.NotSame(roots[0], roots[1]);
            foreach (var root in roots)
            {
                var children = (IList)root.GetType().GetProperty("Children")!.GetValue(root)!;
                Assert.Equal(32, children.Count);
                for (var i = 0; i < children.Count; i++)
                    Assert.Equal("value" + i, children[i]!.GetType().GetProperty("Text")!.GetValue(children[i]));
            }
        }
        finally
        {
            foreach (var root in roots)
                if (XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose();
        }
    }

    private static RoslynTypeSystem Fixture() => new(CompilationFactory.Create("").AddReferences(
        Reference("LibraryZ", LibrarySource), Reference("LibraryA", LibrarySource)), new()
    {
        NamespaceMappings = [new("urn:single", "ResolutionFixture", "LibraryZ"),
            new("urn:multiple", "ResolutionFixture"), new("urn:overlap", "ResolutionFixture", "LibraryZ"),
            new("urn:overlap", "ResolutionFixture"), new("urn:overlap", "ResolutionFixture", "LibraryZ, Version=1.0.0.0")]
    });

    private static Func<string, string, int, TypeResolution> Core(RoslynTypeSystem types) =>
        (Func<string, string, int, TypeResolution>)typeof(RoslynTypeSystem).GetMethod("ResolveCore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<string, string, int, TypeResolution>), types);

    private static TypeResolution Previous(RoslynTypeSystem types, string ns, string name, int arity)
    {
        // Namespace target selection is unchanged. Freeze the previous candidate
        // accumulation/sorting independently; never call the candidate ResolveCore.
        var targets = (ImmutableArray<(string Prefix, IAssemblySymbol Assembly)>)typeof(RoslynTypeSystem)
            .GetMethod("ResolveNamespaceTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(types, [ns])!;
        var candidates = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var metadata = name + (arity == 0 ? string.Empty : "`" + arity);
        foreach (var target in targets)
        {
            var qualified = target.Prefix + metadata;
            var type = target.Assembly.GetTypeByMetadataName(qualified) ?? target.Assembly.ResolveForwardedType(qualified);
            if (type != null && types.IsAccessible(type)) candidates.Add(type);
        }
        var result = candidates.OrderBy(c => c.ContainingAssembly.Identity.ToString(), StringComparer.Ordinal)
            .ThenBy(c => c.MetadataName(), StringComparer.Ordinal).ToImmutableArray();
        return new(result.Length == 1 ? result[0] : null, result);
    }

    private static void Equal(TypeResolution expected, TypeResolution actual)
    {
        Assert.False(actual.Candidates.IsDefault);
        Assert.Equal(expected.IsAmbiguous, actual.IsAmbiguous);
        Assert.True(SymbolEqualityComparer.Default.Equals(expected.Type, actual.Type));
        Assert.Equal(expected.Candidates.Length, actual.Candidates.Length);
        for (var i = 0; i < expected.Candidates.Length; i++)
            Assert.True(SymbolEqualityComparer.Default.Equals(expected.Candidates[i], actual.Candidates[i]));
    }

    private static PortableExecutableReference Reference(string name, string source, params MetadataReference[] references)
    {
        var compilation = CompilationFactory.Create(source).WithAssemblyName(name).AddReferences(references);
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }
}
