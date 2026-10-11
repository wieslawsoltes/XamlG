using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourcePackedGraphTests
{
    [Theory]
    [InlineData("fanin")]
    [InlineData("fanout")]
    [InlineData("islands")]
    [InlineData("lattice")]
    [InlineData("cycles")]
    public void Packed_adjacency_and_reused_counts_preserve_full_diagnostic_order(string shape)
    {
        const int count = 129;
        var fixture = new EmissionAnalysisFixture();
        var documents = Enumerable.Range(0, count).Select(i => fixture.Document with
        {
            Options = fixture.Document.Options with { ResourceUri = "u" + i },
            Root = fixture.Root with { Assignments = Targets(i).Select((target, ordinal) => (BoundAssignment)new BoundSetAssignment(
                fixture.Member, new BoundResourceExpression(new("u" + target, fixture.Root.Type, "source" + target, "Generated", null),
                    new(i * 16 + ordinal, 1)), default)).ToImmutableArray() }
        }).ToArray();
        Check(documents);
        // Competing pre-existing failures must seed in document order; root and
        // source records remain immutable even when graph outcomes are replaced.
        documents[33] = documents[33] with { Diagnostics = [new("TEST", "first", new(33, 1))] };
        documents[97] = documents[97] with { Diagnostics = [new("TEST", "second", new(97, 1))] };
        Check(documents);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => XamlResourceGraph.Validate((BoundDocument[])documents.Clone(), cancelled.Token));
        Check(documents);

        IEnumerable<int> Targets(int index) => shape switch
        {
            "fanin" => index < count - 1 ? new[] { count - 1 } : [],
            "fanout" => index == 0 ? Enumerable.Range(1, count - 1) : [],
            "islands" => index % 8 == 0 && index + 1 < count ? new[] { index + 1 } : [],
            "cycles" => new[] { (index + 1) % count, (index + 1) % count },
            _ => Enumerable.Range(index + 1, Math.Min(4, count - index - 1))
        };
    }

    private static void Check(BoundDocument[] documents)
    {
        var expected = ResourceGraphOracle.Validate((BoundDocument[])documents.Clone(), default);
        var actual = XamlResourceGraph.Validate((BoundDocument[])documents.Clone(), default);
        Compare(documents, expected, actual);
        foreach (var inputs in new[] { documents, actual })
        {
            var original = inputs.Select((document, i) => new XamlEmissionResult("g" + i,
                i is 33 or 97 or 128 ? "" : "source" + i, "Factory" + i, "Build", "Populate", document.Diagnostics,
                [new(new(1, 2), new(i, 1), "source" + i)])
                { SharedSources = [new("Helper" + i, "helper")], PropertyLayout = "layout" + i }).ToArray();
            var expectedDocuments = (BoundDocument[])inputs.Clone();
            var actualDocuments = (BoundDocument[])inputs.Clone();
            var expectedOutputs = (XamlEmissionResult[])original.Clone();
            var actualOutputs = (XamlEmissionResult[])original.Clone();
            ResourceGraphOracle.ValidateEmissions(expectedDocuments, expectedOutputs, default);
            XamlResourceGraph.ValidateEmissions(actualDocuments, actualOutputs, default);
            Compare(inputs, expectedDocuments, actualDocuments);
            for (var i = 0; i < original.Length; i++)
            {
                Assert.Equal(expectedOutputs[i].Source, actualOutputs[i].Source);
                Assert.Equal(expectedOutputs[i].Diagnostics.ToArray(), actualOutputs[i].Diagnostics.ToArray());
                Assert.Equal(expectedOutputs[i].SourceMappings.ToArray(), actualOutputs[i].SourceMappings.ToArray());
                Assert.True(original[i].SharedSources.Equals(actualOutputs[i].SharedSources));
                Assert.Equal(original[i].PropertyLayout, actualOutputs[i].PropertyLayout);
                Assert.Equal(ReferenceEquals(original[i], expectedOutputs[i]), ReferenceEquals(original[i], actualOutputs[i]));
            }
        }
    }

    private static void Compare(BoundDocument[] original, BoundDocument[] expected, BoundDocument[] actual)
    {
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.Equal(expected[i].Diagnostics.ToArray(), actual[i].Diagnostics.ToArray());
            Assert.Equal(expected[i].Success, actual[i].Success);
            Assert.Same(original[i].Root, actual[i].Root);
            Assert.Equal(ReferenceEquals(original[i], expected[i]), ReferenceEquals(original[i], actual[i]));
        }
    }
}
