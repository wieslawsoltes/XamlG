using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

/// <summary>Most fixtures here are graph-only IR, deliberately not executable
/// programs. Separate integration tests execute real generated resource factories.</summary>
public sealed class ResourceDependencyScannerTests
{
    [Fact]
    public void Duplicate_uris_keep_the_first_original_descriptor_and_span_in_reverse_order()
    {
        var f = new Fixture();
        var first = f.Resource("same", 1);
        var last = f.Resource("same", 99) with { Resource = first.Resource with { GeneratedNamespace = "Other" } };
        var upper = f.Resource("SAME", 2);
        var document = f.Document(first, upper, last, first);
        var actual = References(document);
        Assert.Equal(2, actual.Length);
        Assert.Same(first, actual[0]);
        Assert.Same(upper, actual[1]);
        SameFirstReferences(document);
        SameFirstReferences(f.Document(first, upper, last));
        Assert.Same(last, References(f.Document(first, upper, last))[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4096)]
    public void Empty_single_and_many_distinct_uris_keep_initialized_ordered_snapshots(int count)
    {
        var f = new Fixture();
        var document = f.Document(Enumerable.Range(0, count).Select(i => (BoundExpression)f.Resource("u" + i, i)).ToArray());
        var snapshot = References(document);
        Assert.False(snapshot.IsDefault);
        Assert.Equal(count, snapshot.Length);
        SameFirstReferences(document);
    }

    [Fact]
    public void Large_repeated_uri_input_retains_one_representative_without_mutating_occurrences()
    {
        var f = new Fixture();
        var document = f.Document(Enumerable.Range(0, 32768).Select(i => (BoundExpression)f.Resource("external", i)).ToArray());
        var snapshot = References(document);
        Assert.Equal(32768, document.Root!.Assignments.Length);
        Assert.Same(((BoundSetAssignment)document.Root.Assignments[32767]).Value, Assert.Single(snapshot));
        Assert.Equal(new TextSpan(32767, 1), snapshot[0].Span);
    }

    [Fact]
    public void Every_expression_shape_matches_frozen_discovery_including_deferred_and_choice_order()
    {
        var f = new Fixture();
        var a = f.Resource("a", 1); var b = f.Resource("b", 2); var c = f.Resource("a", 3);
        var d = f.Resource("d", 4); var e = f.Resource("e", 5); var z = f.Resource("z", 6);
        var owner = f.Root with { Arguments = [a], Assignments = [new BoundSetAssignment(f.Member, b, default)] };
        var calls = ImmutableArray.Create(new BoundBuilderCall(f.Method, []), new(f.Method, [b, c]), new(f.Method, [d]));
        BoundExpression[] cases =
        [
            a, new BoundConstantExpression(null, f.Root.Type, default),
            new BoundObjectExpression(owner), new BoundMarkupExpression(owner, f.Method, f.Root.Type, default),
            new BoundChoiceExpression(owner, [new(f.Method, c, d), new(f.Method, e, a)], z, f.Root.Type, default),
            new BoundChoiceExpression(f.Root, [], null, f.Root.Type, default),
            new BoundCastExpression(a, f.Root.Type, default), new BoundArrayExpression([a, b, a], f.Array, default),
            new BoundArrayExpression([], f.Array, default),
            new BoundNewExpression(f.Root.Constructor!, [a, b], default) { Initializers = [new(f.Property, c), new(f.Property, d)] },
            new BoundNewExpression(f.Root.Constructor!, [], default),
            new BoundBuilderExpression(a, calls, f.Method, default), new BoundBuilderExpression(a, [], f.Method, default),
            new BoundScopedInitializationExpression(a, f.Method, calls, null, default),
            new BoundCollectionExpression(f.Root.Constructor!, f.Method, f.Property, [a, b], default),
            new BoundCachedExpression("key", a, default), new BoundValueConverterExpression(a, f.Root.Type, f.Root.Type, default),
            new BoundCallExpression(f.Method, a, [b, c], default), new BoundCallExpression(f.Method, null, [a, b], default),
            new BoundLambdaExpression(f.Root.Type, [], a, false, default),
            new BoundPropertyAccessExpression(a, f.Property, [b, c], default), new BoundFieldAccessExpression(a, f.Field, default),
            new BoundAssignmentExpression(a, b, default), new BoundMethodGroupExpression(f.Method, a, f.Root.Type, default),
            new BoundMethodGroupExpression(f.Method, null, f.Root.Type, default), new BoundDeferredExpression(a, f.Root.Type, default),
            new OpaqueExpression(a)
        ];
        foreach (var expression in cases)
        {
            SameFirstReferences(f.Document(expression));
            // Test independent nested wrappers and repeated identities, not only a tree.
            SameFirstReferences(f.Document(new BoundArrayExpression([expression, b, expression], f.Array, default), expression));
        }
        Assert.Empty(References(f.Document(new OpaqueExpression(a))));
    }

    [Fact]
    public void Assignment_descriptors_arguments_postcalls_and_root_arguments_keep_their_order()
    {
        var f = new Fixture();
        var a = f.Resource("a", 1); var b = f.Resource("b", 2); var c = f.Resource("c", 3); var d = f.Resource("d", 4);
        BoundAssignment[] assignments =
        [
            new BoundSetAssignment(f.Member, a, default), new BoundAdaptedSetAssignment(f.Member, a, [], f.Method, default),
            new BoundDynamicSetAssignment(f.Member, b, [], default), new BoundEventAssignment(f.Member, "", null, default) { Value = c },
            new BoundEventAssignment(f.Member, "Handler", null, default),
            new BoundAddAssignment(null, f.Method, [a, b], default) { PostCall = new(f.Method, [], [c, d]) },
            new BoundCallAssignment(f.Method, [b, c], false, default) { TargetDescriptor = a, PostCall = new(f.Method, [], [d]) },
            new BoundRawAssignment("", default)
        ];
        foreach (var assignment in assignments)
            SameFirstReferences(f.Seed with { Root = f.Root with { Arguments = [a, d], Assignments = [assignment] } });
        SameFirstReferences(f.Seed with { Root = f.Root with { Arguments = [d], Assignments = assignments.ToImmutableArray() } });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Shared_dag_with_exponential_occurrence_count_retains_one_dependency(int fanout)
    {
        var f = new Fixture();
        var resource = f.Resource("u", 7);
        BoundExpression value = resource;
        for (var depth = 0; depth < 128; depth++)
            value = new BoundArrayExpression(Enumerable.Repeat(value, fanout).ToImmutableArray(), f.Array, default);
        var document = f.Document(value);
        Assert.Same(resource, Assert.Single(References(document)));
        Assert.True(References(document).Equals(References(document with { Diagnostics = [new("T", "changed", default)] })));
    }

    [Fact]
    public void Distinct_wrappers_over_shared_bound_objects_do_not_reexpand_the_object_graph()
    {
        var f = new Fixture();
        var resource = f.Resource("u", 7);
        var owner = f.Document(resource).Root!;
        for (var depth = 0; depth < 128; depth++)
            owner = f.Root with { Arguments = [new BoundObjectExpression(owner), new BoundMarkupExpression(owner, f.Method, f.Root.Type, default)] };
        Assert.Same(resource, Assert.Single(References(f.Seed with { Root = owner })));
    }

    [Fact]
    public void Unary_depth_is_iterative_and_does_not_use_record_structural_hashing()
    {
        var f = new Fixture();
        var resource = f.Resource("u", 7);
        BoundExpression value = resource;
        for (var depth = 0; depth < 32768; depth++) value = new BoundCastExpression(value, f.Root.Type, default);
        Assert.Same(resource, Assert.Single(References(f.Document(value))));
    }

    [Fact]
    public void Seeded_dags_match_frozen_first_occurrences_and_full_graph_failure_diagnostics()
    {
        var f = new Fixture();
        var random = new Random(1986147);
        for (var sample = 0; sample < 250; sample++)
        {
            var nodes = Enumerable.Range(0, 8).Select(i => ((BoundExpression)f.Resource("u" + random.Next(5), sample * 100 + i), 1)).ToList();
            for (var i = 0; i < 24; i++)
            {
                var (a, ca) = nodes[random.Next(nodes.Count)]; var (b, cb) = nodes[random.Next(nodes.Count)];
                var cost = 3 + ca * 2 + cb;
                if (cost > 2048) continue; // Keep the OLD occurrence oracle bounded, independently of the new algorithm.
                BoundExpression value = (i % 4) switch
                {
                    0 => new BoundArrayExpression([a, b, a], f.Array, default),
                    1 => new BoundChoiceExpression(f.Document(a).Root!, [new(f.Method, a, b)], null, f.Root.Type, default),
                    2 => new BoundCallExpression(f.Method, a, [b, a], default),
                    _ => new BoundArrayExpression([new BoundDeferredExpression(a, f.Root.Type, default), b, a], f.Array, default)
                };
                nodes.Add((value, cost));
            }
            var caller = f.Document(nodes[nodes.Count - 1].Item1, nodes[nodes.Count / 2].Item1) with
                { Options = f.Seed.Options with { ResourceUri = "caller" } };
            SameFirstReferences(caller);
            var documents = new[] { caller }.Concat(Enumerable.Range(0, 5).Select(i =>
                (random.Next(3) == 0 ? f.Document(f.Resource("caller", 900 + i)) : f.Document()) with
                {
                    Options = f.Seed.Options with { ResourceUri = "u" + i },
                    Diagnostics = random.Next(4) == 0 ? [new("PRIOR", "failure", new(i, 1))] : []
                })).ToArray();
            CompareGraphs(documents, sample % documents.Length);
        }
    }

    [Fact]
    public void Cached_dependencies_do_not_cache_local_availability_ambiguity_or_validity()
    {
        var f = new Fixture();
        var first = f.Resource("target", 1); var last = f.Resource("target", 9);
        var caller = f.Document(first, last) with { Options = f.Seed.Options with { ResourceUri = "caller" } };
        var target = f.Document() with { Options = f.Seed.Options with { ResourceUri = "target" } };
        var cycle = f.Document(f.Resource("caller", 10)) with { Options = target.Options };
        var cached = References(caller);
        foreach (var documents in new[] { new[] { caller }, new[] { caller, target }, new[] { caller, cycle },
            new[] { caller, cycle, target }, new[] { caller, target } })
        {
            CompareGraphs(documents, documents.Length - 1);
            Assert.True(cached.Equals(References(caller)));
        }
        Assert.Same(last, Assert.Single(cached));
        Assert.True(XamlResourceGraph.Validate([caller, target], default).All(d => d.Success));
    }

    [Fact]
    public void Cancellation_on_cold_and_warm_paths_does_not_poison_completed_snapshots()
    {
        var f = new Fixture(); var document = f.Document(f.Resource("a", 1), f.Resource("a", 2));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ResourceReferenceCache.Get(document, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => ResourceDependencyScanner.Scan(document.Root!, cancelled.Token));
        var snapshot = References(document);
        Assert.Throws<OperationCanceledException>(() => ResourceReferenceCache.Get(document, cancelled.Token));
        Assert.True(snapshot.Equals(References(document)));
        Assert.Empty(References(document with { Root = null }));
    }

    [Fact]
    public void Concurrent_reads_publish_one_immutable_snapshot_without_shared_scan_state()
    {
        var f = new Fixture();
        var shared = new BoundArrayExpression([f.Resource("a", 1), f.Resource("b", 2)], f.Array, default);
        var document = f.Document(shared, shared, shared);
        var snapshots = new ImmutableArray<BoundResourceExpression>[64];
        Parallel.For(0, snapshots.Length, i => snapshots[i] = References(document));
        Assert.All(snapshots, snapshot => Assert.True(snapshot.Equals(snapshots[0])));
        SameFirstReferences(document);
    }

    [Fact]
    public void Retaining_the_snapshot_does_not_keep_its_root_or_scan_state_alive()
    {
        var f = new Fixture();
        var (root, snapshot) = Populate(f);
        for (var i = 0; i < 12 && root.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(root.IsAlive);
        Assert.Equal(2, snapshot.Length);
        GC.KeepAlive(snapshot);
        GC.KeepAlive(f);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Root, ImmutableArray<BoundResourceExpression> Snapshot) Populate(Fixture f)
    {
        var document = f.Document(f.Resource("a", 1), f.Resource("b", 2), f.Resource("a", 3));
        return (new WeakReference(document.Root!), References(document));
    }

    private static void SameFirstReferences(BoundDocument document)
    {
        var expected = ResourceReferenceOracle.Scan(document, default)
            .GroupBy(resource => resource.Resource.Uri, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var actual = References(document);
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++) Assert.Same(expected[i], actual[i]);
    }

    private static ImmutableArray<BoundResourceExpression> References(BoundDocument document) => ResourceReferenceCache.Get(document, default);

    private static void CompareGraphs(BoundDocument[] original, int failed)
    {
        var expected = ResourceGraphOracle.Validate((BoundDocument[])original.Clone(), default);
        var actual = XamlResourceGraph.Validate((BoundDocument[])original.Clone(), default);
        CompareDocuments(original, expected, actual);
        var before = original.Select((document, i) => new XamlEmissionResult("g" + i, i == failed ? "" : "source" + i,
            "Factory" + i, "Build", "Populate", document.Diagnostics, [])
            { SharedSources = [new("Helper" + i, "helper")], PropertyLayout = "layout" + i }).ToArray();
        var expectedOutputs = (XamlEmissionResult[])before.Clone(); var actualOutputs = (XamlEmissionResult[])before.Clone();
        expected = (BoundDocument[])original.Clone(); actual = (BoundDocument[])original.Clone();
        ResourceGraphOracle.ValidateEmissions(expected, expectedOutputs, default);
        XamlResourceGraph.ValidateEmissions(actual, actualOutputs, default);
        CompareDocuments(original, expected, actual);
        for (var i = 0; i < before.Length; i++)
        {
            Assert.Equal(expectedOutputs[i].Source, actualOutputs[i].Source);
            Assert.Equal(expectedOutputs[i].Diagnostics.ToArray(), actualOutputs[i].Diagnostics.ToArray());
            Assert.Equal(expectedOutputs[i].Success, actualOutputs[i].Success);
            Assert.True(before[i].SharedSources.Equals(actualOutputs[i].SharedSources));
            Assert.Equal(before[i].PropertyLayout, actualOutputs[i].PropertyLayout);
            Assert.Equal(ReferenceEquals(before[i], expectedOutputs[i]), ReferenceEquals(before[i], actualOutputs[i]));
        }
    }

    private static void CompareDocuments(BoundDocument[] before, BoundDocument[] expected, BoundDocument[] actual)
    {
        for (var i = 0; i < before.Length; i++)
        {
            Assert.Equal(expected[i].Diagnostics.ToArray(), actual[i].Diagnostics.ToArray());
            Assert.Equal(expected[i].Success, actual[i].Success);
            Assert.Same(before[i].Root, actual[i].Root);
            Assert.Equal(ReferenceEquals(before[i], expected[i]), ReferenceEquals(before[i], actual[i]));
        }
    }

    private sealed record OpaqueExpression(BoundExpression Hidden) : BoundExpression(Hidden.Type, Hidden.Span);

    private sealed class Fixture
    {
        public BoundDocument Seed { get; }
        public BoundObject Root => Seed.Root!;
        public BoundMember Member { get; }
        public IPropertySymbol Property { get; }
        public IFieldSymbol Field { get; }
        public IMethodSymbol Method { get; }
        public IArrayTypeSymbol Array { get; }
        public Fixture()
        {
            var compilation = CompilationFactory.Create("namespace Dependency { public class Node { public Node Child {get;set;} public Node Field; public static Node Method(Node value) => value; } }");
            Seed = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:Dependency'/>", "Graph.xaml"), compilation);
            Assert.True(Seed.Success, string.Join("\n", Seed.Diagnostics));
            Property = (IPropertySymbol)Root.Type.GetMembers("Child").Single();
            Member = new(Property.Name, BoundMemberKind.Property, Property, Property.Type, Property.GetMethod, Property.SetMethod, default);
            Field = (IFieldSymbol)Root.Type.GetMembers("Field").Single();
            Method = (IMethodSymbol)Root.Type.GetMembers("Method").Single();
            Array = compilation.CreateArrayTypeSymbol(Root.Type);
        }
        public BoundResourceExpression Resource(string uri, int position) => new(new(uri, Root.Type, uri, "Generated", null), new(position, 1));
        public BoundDocument Document(params BoundExpression[] expressions) => Seed with
        { Root = Root with { Assignments = expressions.Select(expression => (BoundAssignment)new BoundSetAssignment(Member, expression, default)).ToImmutableArray() } };
    }
}
