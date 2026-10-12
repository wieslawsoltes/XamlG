using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class NamespaceTargetIndexTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Model = "using System.Collections.Generic; using XamlG.Runtime; namespace TargetFixture { " +
        "public class Root { [Content] public List<Item> Children {get;} = new(); } " +
        "public class Item { public string Text {get;set;} = \"\"; } }";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(1024)]
    public void Target_order_and_assembly_grammar_match_the_frozen_resolver(int count)
    {
        var compilation = CompilationFactory.Create(Model).WithAssemblyName("TargetIndexTests");
        var mappings = Enumerable.Range(0, count).Select(i => new XmlNamespaceMapping("urn:m" + i,
            i % 2 == 0 ? "TargetFixture" : "Missing", compilation.AssemblyName)).ToImmutableArray();
        var types = new RoslynTypeSystem(compilation, new() { NamespaceMappings = mappings });
        foreach (var ns in new[] { "urn:m0", "urn:m" + (count - 1), "urn:missing", "URN:m0", "", "using:TargetFixture",
            "clr-namespace:TargetFixture", "clr-namespace:TargetFixture;assembly=TargetIndexTests",
            "clr-namespace:TargetFixture;assembly= TargetIndexTests , Version=1.0.0.0",
            "clr-namespace:TargetFixture;assembly=Missing;assembly=TargetIndexTests",
            "clr-namespace:TargetFixture;Assembly=Missing", "clr-namespace:TargetFixture;assembly=targetindextests",
            "clr-namespace:TargetFixture;assembly=", "using:", "clr-namespace:" })
            Equal(Previous(types, ns), Core(types)(ns));
    }

    [Fact]
    public void Interleaved_groups_keep_duplicates_and_the_first_assembly_option()
    {
        var compilation = CompilationFactory.Create(Model).WithAssemblyName("TargetIndexTests");
        var entries = ImmutableArray.CreateBuilder<XmlNamespaceMapping>();
        for (var i = 0; i < 128; i++)
        {
            entries.Add(new("urn:shared", i % 2 == 0 ? "TargetFixture" : "Missing", "TargetIndexTests, V=" + i));
            entries.Add(new("urn:other" + i, "Other", null));
        }
        entries.Add(new(null!, "NeverMatches", "TargetIndexTests"));
        entries.Add(new("", "", "TargetIndexTests"));
        var types = new RoslynTypeSystem(compilation, new() { NamespaceMappings = entries.ToImmutable() });
        for (var i = 0; i < 8; i++) Equal(Previous(types, "urn:shared"), Core(types)("urn:shared"));
        Assert.Equal(128, Core(types)("urn:shared").Length);
        Equal(Previous(types, ""), Core(types)(""));
        Assert.Empty(Core(types)("urn:Shared"));
    }

    [Fact]
    public void Unique_groups_need_no_link_array_and_direct_namespaces_do_not_admit_an_index()
    {
        var compilation = CompilationFactory.Create(Model).WithAssemblyName("TargetIndexTests");
        var types = new RoslynTypeSystem(compilation, new()
        {
            XmlnsDefinitionAttributes = [],
            NamespaceMappings = Enumerable.Range(0, 64).Select(i => new XmlNamespaceMapping("urn:" + i, "TargetFixture", "TargetIndexTests")).ToImmutableArray()
        });
        var field = typeof(RoslynTypeSystem).GetField("_namespaceMappingIndex", Hidden)!;
        Assert.Null(field.GetValue(types));
        Core(types)("using:TargetFixture");
        Core(types)("clr-namespace:TargetFixture");
        Assert.Null(field.GetValue(types));
        for (var i = 0; i < 3; i++)
        {
            Assert.Single(Core(types)("urn:" + i));
            Assert.Null(field.GetValue(types));
        }
        Assert.Single(Core(types)("urn:3"));
        var index = field.GetValue(types)!;
        Assert.Null(index.GetType().GetField("_next", Hidden)!.GetValue(index));
    }

    [Fact]
    public void Concurrent_first_queries_and_missing_results_keep_initialized_exact_targets()
    {
        var compilation = CompilationFactory.Create(Model).WithAssemblyName("TargetIndexTests");
        var types = new RoslynTypeSystem(compilation, new()
        {
            NamespaceMappings = Enumerable.Range(0, 512).Select(i => new XmlNamespaceMapping("urn:" + i, "TargetFixture", "TargetIndexTests")).ToImmutableArray()
        });
        var resolve = Core(types);
        var expected = Previous(types, "urn:0");
        Parallel.For(0, 256, i =>
        {
            Equal(expected, resolve("urn:" + i));
            var missing = resolve("urn:absent" + i);
            Assert.False(missing.IsDefault);
            Assert.Empty(missing);
        });
        var replacement = new RoslynTypeSystem(compilation, new());
        Assert.Empty(Core(replacement)("urn:0"));
        Equal(expected, resolve("urn:0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Many_distinct_namespaces_preserve_real_generated_execution(bool shared)
    {
        // XmlnsDefinition attributes exercise mappings discovered from assembly
        // metadata rather than only mappings supplied by configuration.
        var attributes = string.Concat(Enumerable.Range(0, 64).Select(i =>
            "[assembly: XamlG.Runtime.XmlnsDefinition(\"urn:runtime" + i + "\", \"TargetFixture\")]"));
        var source = Model.Replace("namespace TargetFixture", attributes + "namespace TargetFixture");
        var xaml = "<Root xmlns='clr-namespace:TargetFixture'>" + string.Concat(Enumerable.Range(0, 64).Select(i =>
            "<p:Item xmlns:p='urn:runtime" + i + "' Text='value" + i + "'/>")) + "</Root>";
        using var code = CompiledXaml.Create(xaml, source, shareAcrossDocuments: shared);
        var first = code.Build(); var second = code.Build();
        try
        {
            var left = (IList)first.GetType().GetProperty("Children")!.GetValue(first)!;
            var right = (IList)second.GetType().GetProperty("Children")!.GetValue(second)!;
            Assert.Equal(64, left.Count); Assert.Equal(64, right.Count);
            for (var i = 0; i < 64; i++)
            {
                Assert.NotSame(left[i], right[i]);
                Assert.Equal("value" + i, left[i]!.GetType().GetProperty("Text")!.GetValue(left[i]));
            }
        }
        finally
        {
            if (XamlRuntimeSession.TryGet(first, out var a)) a!.Dispose();
            if (XamlRuntimeSession.TryGet(second, out var b)) b!.Dispose();
        }
    }

    private static Func<string, ImmutableArray<(string Prefix, IAssemblySymbol Assembly)>> Core(RoslynTypeSystem types) =>
        (Func<string, ImmutableArray<(string Prefix, IAssemblySymbol Assembly)>>)typeof(RoslynTypeSystem)
            .GetMethod("ResolveNamespaceTargets", Hidden)!.CreateDelegate(typeof(Func<string, ImmutableArray<(string Prefix, IAssemblySymbol Assembly)>>), types);

    private static ImmutableArray<(string Prefix, IAssemblySymbol Assembly)> Previous(RoslynTypeSystem types, string xmlNamespace)
    {
        var assemblies = ImmutableArray.Create(types.Compilation.Assembly).AddRange(types.Compilation.SourceModule.ReferencedAssemblySymbols);
        var byName = assemblies.ToLookup(a => a.Identity.Name, StringComparer.Ordinal);
        var mappings = new List<XmlNamespaceMapping>();
        if (xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var pieces = xmlNamespace.Substring(14).Split(';');
            var assembly = pieces.Skip(1).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal));
            mappings.Add(new(xmlNamespace, pieces[0], assembly?.Substring(9) ?? types.Configuration.DefaultAssemblyName ?? types.Compilation.AssemblyName));
        }
        else if (xmlNamespace.StartsWith("using:", StringComparison.Ordinal)) mappings.Add(new(xmlNamespace, xmlNamespace.Substring(6)));
        else mappings.AddRange(types.NamespaceMappings.Where(m => m.XmlNamespace == xmlNamespace));
        var result = ImmutableArray.CreateBuilder<(string Prefix, IAssemblySymbol Assembly)>();
        foreach (var mapping in mappings)
        {
            var prefix = string.IsNullOrEmpty(mapping.ClrNamespace) ? "" : mapping.ClrNamespace + ".";
            IEnumerable<IAssemblySymbol> selected = assemblies;
            if (mapping.AssemblyName is { } name) selected = byName[name.Split(',')[0].Trim()];
            foreach (var assembly in selected) result.Add((prefix, assembly));
        }
        return result.ToImmutable();
    }

    private static void Equal(ImmutableArray<(string Prefix, IAssemblySymbol Assembly)> expected,
        ImmutableArray<(string Prefix, IAssemblySymbol Assembly)> actual)
    {
        Assert.False(actual.IsDefault);
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        { Assert.Equal(expected[i].Prefix, actual[i].Prefix); Assert.Same(expected[i].Assembly, actual[i].Assembly); }
    }
}
