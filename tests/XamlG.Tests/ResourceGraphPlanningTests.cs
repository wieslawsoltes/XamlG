using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceGraphPlanningTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(1024)]
    public void Independent_and_repeated_local_edges_match_frozen_lowering(int count)
    {
        var fixture = new Fixture();
        var uris = Enumerable.Range(0, count).Select(i => (string?)"u" + i).ToArray();
        var references = Enumerable.Range(0, count).Select(i => i == count - 1
            ? Array.Empty<string>() : Enumerable.Repeat("u" + (i + 1), 17).Append("external").ToArray()).ToArray();
        Check(fixture.Create(uris, references), Array.Empty<int>());
        Check(fixture.Create(uris, references), count == 0 ? Array.Empty<int>() : new[] { count - 1 });
        Check(fixture.Create(uris, references.Select(_ => new[] { "external" }).ToArray()), Array.Empty<int>());
    }

    [Fact]
    public void Seeded_multigraphs_preserve_diagnostics_order_spans_and_backend_failure_closures()
    {
        var fixture = new Fixture();
        var random = new Random(730819);
        for (var sample = 0; sample < 250; sample++)
        {
            var count = random.Next(1, 49);
            var uris = Enumerable.Range(0, count).Select(i => random.Next(6) == 0 ? null : "u" + random.Next(count)).ToArray();
            var references = Enumerable.Range(0, count).Select(_ => Enumerable.Range(0, random.Next(12))
                .Select(_ => random.Next(4) == 0 ? "external" : "u" + random.Next(count)).ToArray()).ToArray();
            var documents = fixture.Create(uris, references);
            for (var i = 0; i < count; i++)
            {
                if (random.Next(11) == 0) documents[i] = documents[i] with { Root = null, IsSkipped = random.Next(2) == 0 };
                if (random.Next(7) == 0) documents[i] = documents[i] with
                { Diagnostics = ImmutableArray.Create(new XamlDiagnostic("TEST", "prior", new(i, 0), random.Next(2) == 0 ? XamlSeverity.Error : XamlSeverity.Warning)) };
            }
            var failed = Enumerable.Range(0, count).Where(_ => random.Next(6) == 0).ToArray();
            Check(documents, failed);
        }
    }

    [Fact]
    public void First_duplicate_edge_and_first_failed_document_determine_the_reported_span()
    {
        var fixture = new Fixture();
        var documents = fixture.Create(new[] { "caller", "first", "second" },
            new[] { new[] { "first", "second", "first" }, Array.Empty<string>(), Array.Empty<string>() });
        documents[1] = documents[1] with { Diagnostics = ImmutableArray.Create(new XamlDiagnostic("TEST", "first failure", default)) };
        documents[2] = documents[2] with { Diagnostics = ImmutableArray.Create(new XamlDiagnostic("TEST", "second failure", default)) };
        var result = XamlResourceGraph.Validate((BoundDocument[])documents.Clone(), default);
        var diagnostic = Assert.Single(result[0].Diagnostics);
        Assert.Equal("XG3305", diagnostic.Code);
        Assert.Equal(new TextSpan(2, 1), diagnostic.Span);
        Assert.EndsWith("first", diagnostic.Message);
        Check(documents, new[] { 1, 2 });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(32)]
    public void Ambiguous_uris_never_select_a_duplicate_target(int duplicates)
    {
        var fixture = new Fixture();
        var uris = Enumerable.Repeat<string?>("duplicate", duplicates).Append("caller").ToArray();
        var references = Enumerable.Range(0, uris.Length).Select(_ => new[] { "duplicate" }).ToArray();
        var documents = fixture.Create(uris, references);
        var actual = (BoundDocument[])documents.Clone();
        XamlResourceGraph.Validate(actual, default);
        for (var i = 0; i < actual.Length; i++) Assert.Same(documents[i], actual[i]);
        Check(documents, new[] { 0 });
    }

    [Fact]
    public void Cycles_and_callers_into_cycles_keep_first_unresolved_edge_diagnostics()
    {
        var documents = new Fixture().Create(new[] { "caller", "cycle", "leaf" },
            new[] { new[] { "cycle", "leaf", "cycle" }, new[] { "cycle", "cycle" }, Array.Empty<string>() });
        var actual = XamlResourceGraph.Validate((BoundDocument[])documents.Clone(), default);
        Assert.Equal("XG3304", Assert.Single(actual[0].Diagnostics).Code);
        Assert.Equal(new TextSpan(2, 1), actual[0].Diagnostics[0].Span);
        Assert.Empty(actual[2].Diagnostics);
        Check(documents, new[] { 2 });
    }

    [Fact]
    public void Failure_propagation_does_not_recurse_through_deep_graphs()
    {
        const int count = 8192;
        var documents = new Fixture().Create(Enumerable.Range(0, count).Select(i => (string?)"u" + i).ToArray(),
            Enumerable.Range(0, count).Select(i => i == count - 1 ? Array.Empty<string>() : new[] { "u" + (i + 1) }).ToArray());
        Check(documents, new[] { count - 1 });
    }

    [Fact]
    public void Cancellation_and_later_revalidation_do_not_cache_graph_validity()
    {
        var documents = new Fixture().Create(new[] { "a", "b" }, new[] { new[] { "b" }, Array.Empty<string>() });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => XamlResourceGraph.Validate((BoundDocument[])documents.Clone(), cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => XamlResourceGraph.ValidateEmissions((BoundDocument[])documents.Clone(), Outputs(documents, new[] { 1 }), cancelled.Token));
        Check(documents, new[] { 1 });
        Check(documents, Array.Empty<int>());
        var failure = documents[1] with { Diagnostics = ImmutableArray.Create(new XamlDiagnostic("TEST", "failure", default)) };
        Check(new[] { documents[0], failure }, Array.Empty<int>());
        Check(documents, Array.Empty<int>());
    }

    private static void Check(BoundDocument[] documents, int[] failedEmissions)
    {
        var expected = (BoundDocument[])documents.Clone();
        var actual = (BoundDocument[])documents.Clone();
        Assert.Same(expected, ResourceGraphOracle.Validate(expected, default));
        Assert.Same(actual, XamlResourceGraph.Validate(actual, default));
        AssertDocuments(documents, expected, actual);
        // Test backend-only failures independently and after ordinary validation.
        Backend(documents, failedEmissions);
        Backend(actual, failedEmissions);
    }

    private static void Backend(BoundDocument[] documents, int[] failures)
    {
        var before = Outputs(documents, failures);
        var expectedDocuments = (BoundDocument[])documents.Clone();
        var actualDocuments = (BoundDocument[])documents.Clone();
        var expected = (XamlEmissionResult[])before.Clone();
        var actual = (XamlEmissionResult[])before.Clone();
        ResourceGraphOracle.ValidateEmissions(expectedDocuments, expected, default);
        XamlResourceGraph.ValidateEmissions(actualDocuments, actual, default);
        AssertDocuments(documents, expectedDocuments, actualDocuments);
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.Equal(expected[i].Source, actual[i].Source);
            Assert.Equal(expected[i].Success, actual[i].Success);
            Assert.Equal(expected[i].Diagnostics.ToArray(), actual[i].Diagnostics.ToArray());
            Assert.Equal(expected[i].SourceMappings.Select(m => (m.GeneratedSpan, m.SourceSpan, m.SourcePath)),
                actual[i].SourceMappings.Select(m => (m.GeneratedSpan, m.SourceSpan, m.SourcePath)));
            Assert.Equal(ReferenceEquals(before[i], expected[i]), ReferenceEquals(before[i], actual[i]));
            Assert.True(actual[i].SharedSources.Equals(before[i].SharedSources));
            Assert.Equal(before[i].PropertyLayout, actual[i].PropertyLayout);
        }
    }

    private static void AssertDocuments(BoundDocument[] original, BoundDocument[] expected, BoundDocument[] actual)
    {
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.Equal(expected[i].Diagnostics.ToArray(), actual[i].Diagnostics.ToArray());
            Assert.Equal(expected[i].Success, actual[i].Success);
            Assert.Same(original[i].Root, actual[i].Root);
            Assert.Equal(ReferenceEquals(original[i], expected[i]), ReferenceEquals(original[i], actual[i]));
        }
    }

    private static XamlEmissionResult[] Outputs(BoundDocument[] documents, int[] failures) =>
        documents.Select((document, i) => new XamlEmissionResult("g" + i, failures.Contains(i) ? "" : "source" + i,
            "Factory" + i, "Build", "Populate", document.Diagnostics,
            ImmutableArray.Create(new XamlSourceMapping(new(1, 2), new(i, 1), "source" + i)))
        {
            IsSkipped = document.IsSkipped,
            SharedSources = ImmutableArray.Create(new SharedGeneratedSource("Helper" + i, "helper")),
            PropertyLayout = "layout" + i
        }).ToArray();

    private sealed class Fixture
    {
        private readonly EmissionAnalysisFixture _seed = new();
        public BoundDocument[] Create(string?[] uris, string[][] references) => uris.Select((uri, i) => _seed.Document with
        {
            Options = _seed.Document.Options with { ResourceUri = uri },
            Root = _seed.Root with { Assignments = references[i].Select((target, ordinal) => (BoundAssignment)new BoundSetAssignment(
                _seed.Member, new BoundResourceExpression(new(target, _seed.Root.Type, target, "Generated", null), new(i * 128 + ordinal, 1)), default)).ToImmutableArray() }
        }).ToArray();
    }
}
