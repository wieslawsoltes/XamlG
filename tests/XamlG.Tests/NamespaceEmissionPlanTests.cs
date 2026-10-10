using System.Collections;
using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class NamespaceEmissionPlanTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        namespace NamespacePlanFixture {
            public interface INamespaces { IReadOnlyDictionary<string,IReadOnlyList<NamespaceItem>> XmlNamespaces {get;} }
            public interface IOtherNamespaces { IReadOnlyDictionary<string,IReadOnlyList<OtherItem>> XmlNamespaces {get;} }
            public class NamespaceItem { public string ClrNamespace {get;set;} public string ClrAssemblyName {get;set;} }
            public class OtherItem { public string ClrNamespace {get;set;} public string ClrAssemblyName {get;set;} }
            public class Owner { public object Value {get;set;} }
            public class SnapshotExtension {
                public object ProvideValue(IServiceProvider provider) => ((INamespaces)provider.GetService(typeof(INamespaces))).XmlNamespaces;
            }
        }
        """;

    private static XamlFrameworkProfile Profile(bool protect) => XamlFrameworkProfile.Portable with
    {
        Runtime = new()
        {
            ProtectNamespaceDictionaries = protect,
            Services = ImmutableArray.Create(
                new XamlServiceMapping("NamespacePlanFixture.INamespaces", XamlServiceKind.XmlNamespaces),
                new XamlServiceMapping("NamespacePlanFixture.IOtherNamespaces", XamlServiceKind.XmlNamespaces))
        }
    };

    private static BoundDocument Document(bool protect, int extraMappings = 0)
    {
        var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Owner xmlns='clr-namespace:NamespacePlanFixture'/>", "Namespaces.xaml"),
            CompilationFactory.Create(Model), Profile(protect));
        Assert.True(bound.Success, string.Join("\n", bound.Diagnostics));
        var mappings = ImmutableArray.Create(
            new XmlNamespaceMapping("urn:one", "First", "A"),
            new XmlNamespaceMapping("urn:unused", "NotUsed", null),
            new XmlNamespaceMapping("urn:one", "Second\"\\\n", null),
            new XmlNamespaceMapping("urn:two", "Third", ""),
            new XmlNamespaceMapping("urn:one", "First", "A"),
            new XmlNamespaceMapping(XamlNames.Xml, "ExplicitXml"));
        return bound with { Runtime = bound.Runtime with { NamespaceMappings = mappings.AddRange(Enumerable.Range(0, extraMappings)
            .Select(i => new XmlNamespaceMapping("urn:extra" + i, "Extra" + i))) } };
    }

    private static NamespaceScope Scope(string text, NamespaceScope? parent = null) =>
        (parent ?? NamespaceScope.Empty).Push(XamlSyntaxTree.Parse(text).Root!);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 10)]
    [InlineData(true, 11)]
    [InlineData(false, 512)]
    [InlineData(true, 512)]
    public void Factories_match_legacy_bytes_for_shadowing_missing_aliases_duplicates_and_contracts(bool protect, int extraMappings)
    {
        using var context = new EmissionContext(Document(protect, extraMappings), default);
        var emitter = new NamespaceMapEmitter(context);
        var root = Scope("<Owner xmlns:z='urn:two' xmlns:a='urn:one' xmlns:b='urn:one' xmlns:m='urn:missing'/>");
        var scopes = new[] { NamespaceScope.Empty, root, Scope("<Owner xmlns:a='urn:two'/>", root),
            Scope("<Owner xmlns:xml='" + XamlNames.Xml + "'/>", root), Scope("<Owner xml:space='preserve'/>", root) };
        // Both cold and warm reads must preserve mapping multiplicity and order.
        for (var pass = 0; pass < 2; pass++)
        foreach (var scope in scopes)
        {
            var expected = new CSharpWriter();
            var actual = new CSharpWriter();
            NamespaceFactoryOracle.EmitFactory(context, expected, scope, "Create", "public");
            emitter.EmitFactory(actual, scope, "Create", "public");
            Assert.Equal(expected.ToString(), actual.ToString());
            var key = string.Join("\n", scope.Bindings.Where(pair => scope.DeclaredPrefixes.Contains(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
            Assert.Equal(key, context.NamespacePlan.GetScope(scope).Key);
        }
    }

    [Fact]
    public void Plans_reuse_scope_snapshots_and_text_but_never_cross_document_mappings()
    {
        var document = Document(false);
        var scope = Scope("<Owner xmlns:a='urn:one'/>");
        using var first = new EmissionContext(document, default);
        using var second = new EmissionContext(document with
        {
            Runtime = document.Runtime with { NamespaceMappings = ImmutableArray.Create(new XmlNamespaceMapping("urn:one", "Changed")) }
        }, default);
        Assert.Same(first.NamespacePlan.GetScope(scope), first.NamespacePlan.GetScope(scope));
        Assert.NotSame(first.NamespacePlan.GetScope(scope), second.NamespacePlan.GetScope(scope));
        var contract = first.NamespacePlan.Contracts[0];
        var expression = contract.GetArrayExpression("urn:one");
        Assert.Same(expression, contract.GetArrayExpression("urn:one"));
        Assert.DoesNotContain("Changed", expression);
        Assert.Contains("Changed", second.NamespacePlan.Contracts[0].GetArrayExpression("urn:one"));
        Assert.DoesNotContain("First", second.NamespacePlan.Contracts[0].GetArrayExpression("urn:one"));
        Assert.Same(contract.GetArrayExpression("missing:one"), contract.GetArrayExpression("missing:two"));
    }

    [Fact]
    public void Shared_and_local_emitters_use_one_plan_without_changing_map_names()
    {
        using var context = new EmissionContext(Document(true), default);
        var scope = Scope("<Owner xmlns:a='urn:one'/>");
        var spaceOnly = Scope("<Owner xml:space='preserve'/>", scope);
        var emitter = new NamespaceMapEmitter(context);
        var name = emitter.GetMap(scope);
        Assert.Equal(name, emitter.GetMap(spaceOnly));
        var snapshot = context.NamespacePlan.GetScope(scope);
        new ServiceContractEmitter(context).CreateShared();
        Assert.Same(snapshot, context.NamespacePlan.GetScope(scope));
        emitter.Emit();
        Assert.Contains(name + "_Create", context.Writer.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_factories_keep_distinct_alias_arrays_and_items(bool protect)
    {
        const string xaml = "<Owner xmlns='clr-namespace:NamespacePlanFixture' xmlns:a='using:Mapped' xmlns:b='using:Mapped' Value='{Snapshot}'/>";
        using var code = CompiledXaml.Create(xaml, Model, Profile(protect));
        var root = code.Build();
        var aliases = (IDictionary)root.GetType().GetProperty("Value")!.GetValue(root)!;
        var a = (IList)aliases["a"]!;
        var b = (IList)aliases["b"]!;
        Assert.NotSame(a, b);
        Assert.Single(a);
        Assert.Single(b);
        Assert.NotSame(a[0], b[0]);
        Assert.Equal("Mapped", a[0]!.GetType().GetProperty("ClrNamespace")!.GetValue(a[0]));
        if (protect) Assert.Throws<NotSupportedException>(() => a[0] = b[0]);
        if (XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(512)]
    public void Mapping_index_promotion_is_bounded_and_tiny_tables_stay_scan_only(int extraMappings)
    {
        using var context = new EmissionContext(Document(false, extraMappings), default);
        var plan = context.NamespacePlan;
        var contract = plan.Contracts[0];
        var index = typeof(NamespaceEmissionPlan).GetField("_mappings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var first = contract.GetArrayExpression("urn:one");
        // An initializer-cache hit is not another mapping scan.
        for (var i = 0; i < 32; i++) Assert.Same(first, contract.GetArrayExpression("urn:one"));
        for (var i = 0; i < 3; i++) contract.GetArrayExpression("missing:" + i);
        Assert.Null(index.GetValue(plan));
        contract.GetArrayExpression("missing:fourth");
        if (extraMappings <= 10) Assert.Null(index.GetValue(plan));
        else Assert.NotNull(index.GetValue(plan));
        Assert.Same(first, contract.GetArrayExpression("urn:one"));
    }

    [Fact]
    public void Canceled_plan_reads_reject_cached_results()
    {
        using var cancellation = new CancellationTokenSource();
        using var context = new EmissionContext(Document(true), cancellation.Token);
        var scope = Scope("<Owner xmlns:a='urn:one'/>");
        _ = context.NamespacePlan.GetScope(scope);
        var contract = context.NamespacePlan.Contracts[0];
        _ = contract.GetArrayExpression("urn:one");
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => context.NamespacePlan.GetScope(scope));
        Assert.Throws<OperationCanceledException>(() => contract.GetArrayExpression("urn:one"));
        Assert.Throws<OperationCanceledException>(() => { _ = context.NamespacePlan.Contracts; });
    }

    [Fact]
    public void Concurrent_document_emission_has_no_shared_mutable_planning_state()
    {
        var document = Document(true);
        var sources = new string[16];
        Parallel.For(0, sources.Length, index => sources[index] = new CSharpEmitter().Emit(document).Source);
        Assert.All(sources, source => Assert.Equal(sources[0], source));
    }
}
