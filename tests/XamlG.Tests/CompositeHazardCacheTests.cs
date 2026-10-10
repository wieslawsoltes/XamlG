using XamlG.Compiler;
using XamlG.CSharp;
using Xunit;

namespace XamlG.Tests;

public sealed class CompositeHazardCacheTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void RepeatedCompositeDagIsAnalyzedByIdentityRatherThanByOccurrence(int arity)
    {
        var f = new EmissionAnalysisFixture();
        BoundExpression value = new BoundConstantExpression(null, f.Root.Type, default);
        // Only 33 distinct expressions; even binary expansion exceeds four billion.
        // This is an analysis test, not a request to emit an exponentially large program.
        for (var i = 0; i < 32; i++) value = new BoundArrayExpression([.. Enumerable.Repeat(value, arity)], f.ArrayType, default);
        var assignment = new BoundSetAssignment(f.Member, value, default);
        // Bound a regression's work: an accidentally restored occurrence walk
        // must cancel rather than pinning the entire native test job indefinitely.
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var context = new EmissionContext(f.Document, watchdog.Token);
        Assert.False(context.Locals.ContainsReference(assignment));
        Assert.False(context.Locals.ContainsReference(assignment with { Span = new(1, 0) }));
        Assert.Equal(arity <= 2 ? 8 : 32, CacheCount(context.Locals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredReferenceMaskNeverChangesTheSharedChildResult(bool deferredFirst)
    {
        var f = new EmissionAnalysisFixture();
        BoundExpression shared = new BoundReferenceExpression("name", f.Root.Type, default);
        for (var i = 0; i < 16; i++) shared = new BoundArrayExpression([shared, shared], f.ArrayType, default);
        var direct = new BoundSetAssignment(f.Member, shared, default);
        var deferred = direct with { Value = new BoundDeferredExpression(shared, f.Root.Type, default) };
        using var context = new EmissionContext(f.Document, default);
        if (deferredFirst)
        {
            Assert.False(context.Locals.ContainsReference(deferred));
            Assert.True(context.Locals.ContainsReference(direct));
        }
        else
        {
            Assert.True(context.Locals.ContainsReference(direct));
            Assert.False(context.Locals.ContainsReference(deferred));
        }
    }

    [Fact]
    public void SharedSubtreesDoNotHideRawCodeInALaterSibling()
    {
        var f = new EmissionAnalysisFixture();
        BoundExpression shared = new BoundReferenceExpression("captured", f.Root.Type, default);
        for (var i = 0; i < 16; i++) shared = new BoundArrayExpression([shared, shared], f.ArrayType, default);
        var dangerous = new BoundSetAssignment(f.Member, new BoundArrayExpression(
            [shared, new BoundRawExpression("default!", f.Root.Type, default)], f.ArrayType, default), default);
        var safe = new BoundSetAssignment(f.Member, new BoundConstantExpression(null, f.Root.Type, default), default);
        var root = f.Root with { Assignments = [dangerous, safe] };
        using var context = new EmissionContext(f.Document with { Root = root }, default);
        string first, second;
        using (context.Locals.EnterAssignment(root, safe)) first = context.Locals.Declare("object", "null", "value");
        using (context.Locals.EnterAssignment(root, safe)) second = context.Locals.Declare("object", "null", "value");
        Assert.NotEqual(first, second); // Raw code disables reuse document-wide.
    }

    [Fact]
    public void HarmlessSharedCompositesStillAllowTemporaryReuse()
    {
        var f = new EmissionAnalysisFixture();
        BoundExpression value = new BoundConstantExpression(null, f.Root.Type, default);
        for (var i = 0; i < 16; i++) value = new BoundArrayExpression([value, value], f.ArrayType, default);
        var assignment = new BoundSetAssignment(f.Member, value, default);
        var root = f.Root with { Assignments = [assignment] };
        using var context = new EmissionContext(f.Document with { Root = root }, default);
        string first, second;
        using (context.Locals.EnterAssignment(root, assignment)) first = context.Locals.Declare("object", "null", "value");
        using (context.Locals.EnterAssignment(root, assignment)) second = context.Locals.Declare("object", "null", "value");
        Assert.Equal(first, second);
    }

    [Fact]
    public void SharedHazardsMatchAnIndependentCompositionalOracle()
    {
        var f = new EmissionAnalysisFixture();
        var analyze = typeof(TemporaryLocalPool).GetMethod("Analyze",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(BoundExpression)], null)!;
        // Build expected flags alongside the DAG, independently of the production
        // child enumerator and cache. Check warm and cold contexts in reverse order.
        const int capture = 1, raw = 2, reference = 4;
        var nodes = new List<(BoundExpression Value, int Flags)>
        {
            (new BoundConstantExpression(null, f.Root.Type, default), 0),
            (new BoundRawExpression("default!", f.Root.Type, default), raw),
            (new BoundReferenceExpression("name", f.Root.Type, default), capture | reference)
        };
        var random = new Random(81763);
        for (var i = 0; i < 160; i++)
        {
            var left = nodes[random.Next(nodes.Count)];
            var right = nodes[random.Next(nodes.Count)];
            if (i % 3 == 0)
            {
                System.Collections.Immutable.ImmutableArray<BoundExpression> children = (i % 4) switch
                {
                    0 => [left.Value],
                    1 => [left.Value, right.Value],
                    2 => [left.Value, right.Value, left.Value],
                    _ => [left.Value, right.Value, left.Value, right.Value, left.Value]
                };
                nodes.Add((new BoundArrayExpression(children, f.ArrayType, default),
                    children.Length == 1 ? left.Flags : left.Flags | right.Flags));
            }
            else if (i % 3 == 1)
                nodes.Add((new BoundCastExpression(left.Value, f.Root.Type, default), left.Flags));
            else
                nodes.Add((new BoundDeferredExpression(left.Value, f.Root.Type, default), (left.Flags | capture) & ~reference));
        }
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var warm = new EmissionContext(f.Document, watchdog.Token);
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            using var cold = new EmissionContext(f.Document, watchdog.Token);
            Assert.Equal(nodes[i].Flags, Convert.ToInt32(analyze.Invoke(warm.Locals, [nodes[i].Value])));
            Assert.Equal(nodes[i].Flags, Convert.ToInt32(analyze.Invoke(cold.Locals, [nodes[i].Value])));
        }
    }

    [Theory]
    [InlineData(8, 17)]
    [InlineData(12, 273)]
    public void UnsharedBinaryTreesRetainOnlyBoundedSegmentRoots(int depth, int expectedEntries)
    {
        var f = new EmissionAnalysisFixture();
        BoundExpression Build(int level) => level == 0
            ? new BoundConstantExpression(null, f.Root.Type, default)
            : new BoundArrayExpression([Build(level - 1), Build(level - 1)], f.ArrayType, default);
        using var context = new EmissionContext(f.Document, default);
        Assert.False(context.Locals.ContainsReference(new BoundSetAssignment(f.Member, Build(depth), default)));
        Assert.Equal(expectedEntries, CacheCount(context.Locals));
    }

    private static int CacheCount(TemporaryLocalPool pool)
    {
        var field = typeof(TemporaryLocalPool).GetField("_expressions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(pool)).Count;
    }

    [Fact]
    public void CachedCompositeLookupsStillObserveCancellation()
    {
        var f = new EmissionAnalysisFixture();
        var shared = new BoundCastExpression(new BoundReferenceExpression("name", f.Root.Type, default), f.Root.Type, default);
        var assignment = new BoundSetAssignment(f.Member, shared, default);
        using var cancellation = new CancellationTokenSource();
        using var context = new EmissionContext(f.Document, cancellation.Token);
        Assert.True(context.Locals.ContainsReference(assignment));
        cancellation.Cancel();
        // A distinct assignment enters expression analysis rather than the old
        // assignment-result fast path. No partial new result is published.
        Assert.Throws<OperationCanceledException>(() => context.Locals.ContainsReference(assignment with { Span = new(2, 0) }));
        using var next = new EmissionContext(f.Document, default);
        Assert.True(next.Locals.ContainsReference(assignment));
    }
}
