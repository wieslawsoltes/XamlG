using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceReverseEdgesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(35)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(131)]
    [InlineData(132)]
    [InlineData(133)]
    [InlineData(1024)]
    public void Interleaved_targets_preserve_exact_append_order_across_growth(int count)
    {
        var fixture = new EmissionAnalysisFixture();
        var graph = new ResourceReverseEdges(7);
        var edges = Enumerable.Range(0, count).Select(i => new BoundResourceExpression(
            new("u" + i, fixture.Root.Type, "source" + i, "Generated", null), new(i, 1))).ToArray();
        for (var i = 0; i < count; i++) graph.Add(i % 5, i, edges[i]);
        for (var target = 0; target < 7; target++)
        {
            var expected = Enumerable.Range(0, count).Where(i => i % 5 == target).ToArray();
            var index = 0;
            foreach (var incoming in graph.Incoming(target))
            {
                Assert.Equal(expected[index], incoming.Parent);
                Assert.Same(edges[expected[index]], incoming.Edge);
                index++;
            }
            Assert.Equal(expected.Length, index);
        }
    }

    [Fact]
    public void Storage_does_not_deduplicate_equal_descriptors_or_parent_values()
    {
        var fixture = new EmissionAnalysisFixture();
        var edge = new BoundResourceExpression(new("same", fixture.Root.Type, "same", "Generated", null), new(1, 2));
        var distinct = edge with { };
        var graph = new ResourceReverseEdges(1);
        graph.Add(0, 0, edge); graph.Add(0, 0, distinct); graph.Add(0, 0, edge);
        var actual = new List<BoundResourceExpression>();
        foreach (var incoming in graph.Incoming(0)) actual.Add(incoming.Edge);
        Assert.Equal(3, actual.Count);
        Assert.Same(edge, actual[0]); Assert.Same(distinct, actual[1]); Assert.Same(edge, actual[2]);
    }

    [Fact]
    public void Completed_graph_enumeration_allocates_nothing()
    {
        var fixture = new EmissionAnalysisFixture();
        var edge = new BoundResourceExpression(new("u", fixture.Root.Type, "u", "Generated", null), default);
        var graph = new ResourceReverseEdges(2);
        for (var i = 0; i < 37; i++) graph.Add(i % 2, i, edge);
        var checksum = 0;
        for (var i = 0; i < 128; i++) foreach (var incoming in graph.Incoming(i % 2)) checksum ^= incoming.Parent;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2048; i++) foreach (var incoming in graph.Incoming(i % 2)) checksum ^= incoming.Parent;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Edge_blocks_are_bounded_and_are_not_copied_when_the_directory_grows()
    {
        var fixture = new EmissionAnalysisFixture();
        var edge = new BoundResourceExpression(new("u", fixture.Root.Type, "u", "Generated", null), default);
        var graph = new ResourceReverseEdges(1);
        for (var i = 0; i < 36; i++) graph.Add(0, i, edge);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var firstField = typeof(ResourceReverseEdges).GetField("_first", flags)!;
        var blocksField = typeof(ResourceReverseEdges).GetField("_blocks", flags)!;
        var first = (Array)firstField.GetValue(graph)!;
        var directory = (Array)blocksField.GetValue(graph)!;
        var initialBlock = directory.GetValue(0);
        Assert.Equal(4, first.Length);
        Assert.Equal(32, Assert.IsAssignableFrom<Array>(initialBlock).Length);
        for (var i = 36; i < 1024; i++) graph.Add(0, i, edge);
        var grown = (Array)blocksField.GetValue(graph)!;
        Assert.Same(first, firstField.GetValue(graph));
        Assert.NotSame(directory, grown);
        Assert.Same(initialBlock, grown.GetValue(0));
        foreach (var block in grown)
            if (block != null) Assert.Equal(32, Assert.IsAssignableFrom<Array>(block).Length);
        var count = 0;
        foreach (var incoming in graph.Incoming(0))
        {
            Assert.Equal(count++, incoming.Parent);
            Assert.Same(edge, incoming.Edge);
        }
        Assert.Equal(1024, count);
    }

    [Fact]
    public void Negative_document_counts_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceReverseEdges(-1));
}
