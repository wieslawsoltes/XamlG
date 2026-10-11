using XamlG.CSharp.Resources;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceVisitFilterTests
{
    [Fact]
    public void Repeated_nodes_expand_at_most_twice_even_when_every_fingerprint_collides()
    {
        var filter = new ResourceVisitFilter();
        var nodes = Enumerable.Range(0, 4096).Select(_ => new object()).ToArray();
        var visits = new int[nodes.Length];
        for (var round = 0; round < 8; round++)
            for (var i = 0; i < nodes.Length; i++)
                if (filter.TryExpand(nodes[i], 0)) visits[i]++;
        Assert.Equal(2, visits[0]);
        Assert.All(visits.Skip(1), count => Assert.Equal(1, count));
        Assert.Equal(nodes.Length, filter.TrackedCount);
    }

    [Fact]
    public void Distinct_records_with_equal_values_are_never_confused_by_the_exact_set()
    {
        var filter = new ResourceVisitFilter();
        var first = new EqualRecord(1); var second = new EqualRecord(1);
        Assert.True(filter.TryExpand(first, 0));
        Assert.True(filter.TryExpand(second, 0));
        Assert.True(filter.TryExpand(first, 0));
        Assert.False(filter.TryExpand(second, 0));
        Assert.False(filter.TryExpand(first, 0));
        Assert.Equal(2, filter.TrackedCount);
    }

    [Fact]
    public void A_saturated_filter_still_expands_every_new_identity_without_false_pruning()
    {
        var filter = new ResourceVisitFilter();
        // First fingerprint bits alone cover all 1,024 slots. Subsequent probes
        // must still expand previously unseen nodes despite a 100% filter hit rate.
        var nodes = Enumerable.Range(0, 1024).Select(_ => new object()).ToArray();
        for (var i = 0; i < nodes.Length; i++) Assert.True(filter.TryExpand(nodes[i], i));
        for (var i = 0; i < 8192; i++)
        {
            var node = new object();
            Assert.True(filter.TryExpand(node, i));
            Assert.False(filter.TryExpand(node, i));
        }
    }

    [Fact]
    public void Unshared_nodes_without_filter_collisions_do_not_allocate_exact_set_entries()
    {
        var filter = new ResourceVisitFilter();
        var nodes = new List<object>();
        var occupied = new bool[1024];
        for (uint hash = 0; nodes.Count < 64; hash++)
        {
            var mixed = unchecked(hash * 0x9e3779b9U); mixed ^= mixed >> 16;
            var first = (int)(hash & 1023); var second = (int)(mixed & 1023);
            if (occupied[first] || occupied[second]) continue;
            occupied[first] = occupied[second] = true;
            var node = new object(); nodes.Add(node);
            Assert.True(filter.TryExpand(node, (int)hash));
        }
        Assert.Equal(0, filter.TrackedCount);
        GC.KeepAlive(nodes);
    }

    [Fact]
    public void Seeded_interleavings_never_expand_an_identity_more_than_twice()
    {
        var random = new Random(714971);
        for (var run = 0; run < 32; run++)
        {
            var filter = new ResourceVisitFilter();
            var nodes = Enumerable.Range(0, 512).Select(_ => new object()).ToArray();
            var fingerprints = nodes.Select(_ => random.Next(64)).ToArray();
            var expansions = new int[nodes.Length];
            for (var i = 0; i < 8192; i++)
            {
                var index = random.Next(nodes.Length);
                if (filter.TryExpand(nodes[index], fingerprints[index])) expansions[index]++;
                Assert.InRange(expansions[index], 1, 2);
            }
            for (var i = 0; i < nodes.Length; i++)
            {
                if (filter.TryExpand(nodes[i], fingerprints[i])) expansions[i]++;
                Assert.InRange(expansions[i], 1, 2);
            }
        }
    }

    private sealed record EqualRecord(int Value);
}
