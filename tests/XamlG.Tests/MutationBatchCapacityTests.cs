using System.Collections;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class MutationBatchCapacityTests
{
    private sealed class Box(int id) { public int Id { get; } = id; public int Value = id; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Large_known_batches_preserve_reverse_rollback_and_original_targets(bool asList)
    {
        var boxes = Enumerable.Range(0, 1500).Select(i => new Box(i)).ToArray();
        var writes = new List<int>();
        var table = new XamlPropertyTable(new[] { typeof(int) }, static (target, _) => ((Box)target).Value,
            (target, _, value) =>
            {
                var box = (Box)target; box.Value = (int)value!; writes.Add(box.Id);
                if (box.Id == 1400 && box.Value == -1) throw new InvalidOperationException("setter failed after writing");
            });
        using var session = new XamlRuntimeSession();
        for (var i = 0; i < boxes.Length; i++) table.Register(session, "n" + i, "Value", boxes[i], 0);
        var updates = Enumerable.Range(0, boxes.Length).Select(i => new XamlPropertyUpdate("n" + i, "Value", -1)).ToArray();
        IReadOnlyList<XamlPropertyUpdate> batch = asList ? updates.ToList() : updates;
        var result = session.Apply(0, batch);
        Assert.False(result.Applied); Assert.Equal(0, session.Revision);
        Assert.Equal(Enumerable.Range(0, 1401).Concat(Enumerable.Range(0, 1401).Reverse()), writes);
        Assert.All(boxes, box => Assert.Equal(box.Id, box.Value));
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate("n1499", "Value", 42) }).Applied);
        Assert.Equal(42, boxes[1499].Value);
    }

    [Fact]
    public void Unknown_read_only_lists_do_not_gain_observable_count_or_indexer_reads()
    {
        var value = 1;
        using var session = new XamlRuntimeSession();
        session.RegisterProperty("node", "Value", () => value, replacement => value = replacement);
        var batch = new EnumerationOnly(new[] { new XamlPropertyUpdate("node", "Value", 2) });
        Assert.True(session.Apply(0, batch).Applied);
        Assert.Equal(2, value); Assert.Equal(1, batch.Enumerations);
        Assert.False(session.Apply(0, batch).Applied);
        Assert.Equal(1, batch.Enumerations);
    }

    [Fact]
    public void Early_duplicate_in_a_large_array_never_evaluates_later_getters_or_setters()
    {
        var reads = 0; var writes = 0;
        using var session = new XamlRuntimeSession();
        session.RegisterProperty("node", "Value", () => { reads++; return 0; }, _ => writes++);
        var update = new XamlPropertyUpdate("node", "Value", 1);
        var batch = Enumerable.Repeat(update, 65536).ToArray();
        Assert.False(session.Apply(0, batch).Applied);
        Assert.Equal(1, reads); Assert.Equal(0, writes); Assert.Equal(0, session.Revision);
    }

    private sealed class EnumerationOnly(IEnumerable<XamlPropertyUpdate> values) : IReadOnlyList<XamlPropertyUpdate>
    {
        public int Count => throw new InvalidOperationException("Count must not be read");
        public XamlPropertyUpdate this[int index] => throw new InvalidOperationException("Indexer must not be read");
        public int Enumerations { get; private set; }
        public IEnumerator<XamlPropertyUpdate> GetEnumerator() { Enumerations++; return values.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
