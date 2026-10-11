using System.Reflection;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceConcentratedEdgesTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(133)]
    [InlineData(1024)]
    public void Late_promotion_keeps_implicit_prefix_and_appended_links_in_exact_order(int prefix)
    {
        var fixture = new EmissionAnalysisFixture();
        var edge = new BoundResourceExpression(new("u", fixture.Root.Type, "source", "Generated", null), new(1, 2));
        var graph = new ResourceReverseEdges(4);
        var expected = Enumerable.Range(0, 4).Select(_ => new List<(int Parent, BoundResourceExpression Edge)>()).ToArray();
        for (var i = 0; i < prefix; i++) Add(1, i);
        Assert.Null(typeof(ResourceReverseEdges).GetField("_targets", Private)!.GetValue(graph));
        Check();
        Add(2, prefix);
        Add(1, prefix + 1);
        Add(3, prefix + 2);
        Add(2, prefix + 3);
        Add(1, prefix + 4);
        Assert.NotNull(typeof(ResourceReverseEdges).GetField("_targets", Private)!.GetValue(graph));
        Check();

        void Add(int target, int parent)
        {
            // Equal records must retain their distinct reference identities.
            var value = edge with { };
            expected[target].Add((parent, value));
            graph.Add(target, parent, value);
        }
        void Check()
        {
            for (var target = 0; target < expected.Length; target++)
            {
                var index = 0;
                foreach (var incoming in graph.Incoming(target))
                {
                    Assert.Equal(expected[target][index].Parent, incoming.Parent);
                    Assert.Same(expected[target][index++].Edge, incoming.Edge);
                }
                Assert.Equal(expected[target].Count, index);
            }
        }
    }

    [Fact]
    public void Single_destination_uses_no_endpoint_array_and_enumeration_allocates_nothing()
    {
        var fixture = new EmissionAnalysisFixture();
        var edge = new BoundResourceExpression(new("u", fixture.Root.Type, "source", "Generated", null), default);
        var graph = new ResourceReverseEdges(4096);
        for (var i = 0; i < 1024; i++) graph.Add(3000, i, edge);
        Assert.Null(typeof(ResourceReverseEdges).GetField("_targets", Private)!.GetValue(graph));
        var checksum = 0;
        for (var iteration = 0; iteration < 16; iteration++)
            foreach (var incoming in graph.Incoming(3000)) checksum ^= incoming.Parent;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 64; iteration++)
            foreach (var incoming in graph.Incoming(3000)) checksum ^= incoming.Parent;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Value_cursor_copies_keep_independent_entries_and_positions(bool distributed)
    {
        var fixture = new EmissionAnalysisFixture();
        var edge = new BoundResourceExpression(new("u", fixture.Root.Type, "source", "Generated", null), default);
        var graph = new ResourceReverseEdges(2);
        var references = new List<BoundResourceExpression>();
        for (var i = 0; i < 80; i++)
        {
            var current = edge with { };
            graph.Add(distributed ? i % 2 : 0, i, current);
            if (!distributed || i % 2 == 0) references.Add(current);
        }
        var left = graph.Incoming(0);
        Assert.True(left.MoveNext());
        var right = left;
        Assert.Same(references[0], right.Current.Edge);
        Assert.True(left.MoveNext());
        Assert.Same(references[1], left.Current.Edge);
        Assert.Same(references[0], right.Current.Edge);
        for (var i = 1; i < references.Count; i++)
        {
            Assert.True(right.MoveNext());
            Assert.Same(references[i], right.Current.Edge);
        }
        Assert.False(right.MoveNext());
        Assert.False(right.MoveNext());
        Assert.Same(references[1], left.Current.Edge);
    }

    [Fact]
    public void Invalid_targets_do_not_publish_entries_before_or_after_promotion()
    {
        var fixture = new EmissionAnalysisFixture();
        var edge = new BoundResourceExpression(new("u", fixture.Root.Type, "source", "Generated", null), default);
        var graph = new ResourceReverseEdges(2);
        foreach (var count in new[] { 0, 1, 2 })
        {
            Assert.Throws<IndexOutOfRangeException>(() => graph.Add(-1, 0, edge));
            Assert.Throws<IndexOutOfRangeException>(() => graph.Add(2, 0, edge));
            Assert.Equal(count, (int)typeof(ResourceReverseEdges).GetField("_count", Private)!.GetValue(graph)!);
            if (count < 2) graph.Add(count, count, edge);
        }
        Assert.Throws<IndexOutOfRangeException>(() => graph.Incoming(-1));
        Assert.Throws<IndexOutOfRangeException>(() => graph.Incoming(2));
    }
}
