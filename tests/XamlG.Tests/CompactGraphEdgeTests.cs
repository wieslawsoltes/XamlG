using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class CompactGraphEdgeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Unique_uri_edges_preserve_case_distinctions_ambiguity_and_shared_target_roots(int mode)
    {
        var compilation = CompilationFactory.Create("namespace CompactEdges { public class Node { public Node Child {get;set;} } }");
        var seed = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:CompactEdges'/>", "Graph.xaml"), compilation);
        Assert.True(seed.Success, string.Join("\n", seed.Diagnostics));
        var root = seed.Root!;
        var property = (IPropertySymbol)root.Type.GetMembers("Child").Single();
        var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type, property.GetMethod, property.SetMethod, default);
        string[] uris = mode switch
        {
            0 => ["target", "TARGET"],
            1 => ["target", "target"],
            _ => ["target", "target", "other"]
        };
        var edges = new[] { "target", "TARGET", "other", "target", "other", "target", "TARGET" }
            .Select((uri, index) => new BoundResourceExpression(new(uri, root.Type, "same-local-id", "Generated", null), new(index, 1)))
            .ToArray();
        var caller = seed with
        {
            Options = seed.Options with { ResourceUri = "caller" },
            Root = root with { Assignments = edges.Select(edge => (BoundAssignment)new BoundSetAssignment(member, edge, edge.Span)).ToImmutableArray() }
        };
        // Different declared URIs may share the SAME empty BoundObject and local
        // descriptor id. They still represent distinct target document indexes.
        var documents = new[] { caller }.Concat(uris.Select(uri => seed with { Options = seed.Options with { ResourceUri = uri } })).ToArray();
        var snapshot = ResourceReferenceCache.Get(caller, default);
        Assert.Equal(3, snapshot.Length);
        Assert.Same(edges[6], snapshot[0]);
        Assert.Same(edges[5], snapshot[1]);
        Assert.Same(edges[4], snapshot[2]);
        var expected = ResourceGraphOracle.Validate((BoundDocument[])documents.Clone(), default);
        var actual = XamlResourceGraph.Validate((BoundDocument[])documents.Clone(), default);
        Compare(expected, actual);
        Assert.All(actual, document => Assert.True(document.Success));

        for (var failed = 1; failed < documents.Length; failed++)
        {
            var outputs = documents.Select((document, i) => new XamlEmissionResult("g" + i, i == failed ? "" : "source",
                "Factory" + i, "Build", "Populate", [], [])).ToArray();
            var expectedOutputs = (XamlEmissionResult[])outputs.Clone();
            var actualOutputs = (XamlEmissionResult[])outputs.Clone();
            expected = (BoundDocument[])documents.Clone();
            actual = (BoundDocument[])documents.Clone();
            ResourceGraphOracle.ValidateEmissions(expected, expectedOutputs, default);
            XamlResourceGraph.ValidateEmissions(actual, actualOutputs, default);
            Compare(expected, actual);
            for (var i = 0; i < outputs.Length; i++)
            {
                Assert.Equal(expectedOutputs[i].Source, actualOutputs[i].Source);
                Assert.Equal(expectedOutputs[i].Diagnostics.ToArray(), actualOutputs[i].Diagnostics.ToArray());
                Assert.Equal(expectedOutputs[i].Success, actualOutputs[i].Success);
                Assert.Equal(ReferenceEquals(outputs[i], expectedOutputs[i]), ReferenceEquals(outputs[i], actualOutputs[i]));
            }
        }
    }

    private static void Compare(BoundDocument[] expected, BoundDocument[] actual)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Diagnostics.ToArray(), actual[i].Diagnostics.ToArray());
            Assert.Equal(expected[i].Success, actual[i].Success);
            Assert.Same(expected[i].Root, actual[i].Root);
        }
    }
}
