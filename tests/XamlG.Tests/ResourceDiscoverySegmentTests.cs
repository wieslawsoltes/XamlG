using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceDiscoverySegmentTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void Unary_segments_bound_exact_retention_independently_of_fingerprint_collisions(int depth)
    {
        var (seed, array) = Seed();
        var resource = new BoundResourceExpression(new("target", seed.Root!.Type, "target", "Generated", null), new(4, 1));
        BoundExpression value = resource;
        for (var i = 0; i < depth; i++) value = new BoundCastExpression(value, seed.Root.Type, default);
        var (snapshot, retained) = Scan(seed.Root with { Arguments = [value] });
        Assert.Same(resource, Assert.Single(snapshot));
        Assert.InRange(retained, 0, depth / 32);
        GC.KeepAlive(array);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(10, 32)]
    public void Binary_segments_split_the_work_budget_instead_of_allowing_exponential_depth(int depth, int maximumEntries)
    {
        var (seed, array) = Seed();
        var resource = new BoundResourceExpression(new("target", seed.Root!.Type, "target", "Generated", null), new(4, 1));
        // Every aggregate is distinct. The resource leaf can be shared without
        // changing the number of visited aggregate identities or retained entries.
        BoundExpression Tree(int level) => level == 0 ? resource : new BoundArrayExpression([Tree(level - 1), Tree(level - 1)], array, default);
        var (snapshot, retained) = Scan(seed.Root with { Arguments = [Tree(depth)] });
        Assert.Same(resource, Assert.Single(snapshot));
        Assert.InRange(retained, 0, maximumEntries);
    }

    private static (ImmutableArray<BoundResourceExpression> Snapshot, int Retained) Scan(BoundObject root)
    {
        var scanner = Activator.CreateInstance(typeof(ResourceDependencyScanner), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: new object[] { CancellationToken.None }, culture: null)!;
        var snapshot = (ImmutableArray<BoundResourceExpression>)typeof(ResourceDependencyScanner)
            .GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scanner, new object[] { root })!;
        var visits = (ResourceVisitFilter)typeof(ResourceDependencyScanner)
            .GetField("_visits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        return (snapshot, visits.TrackedCount);
    }

    private static (BoundDocument Seed, IArrayTypeSymbol Array) Seed()
    {
        var compilation = CompilationFactory.Create("namespace DiscoverySegments { public class Node {} }");
        var seed = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:DiscoverySegments'/>", "Segment.xaml"), compilation);
        Assert.True(seed.Success, string.Join("\n", seed.Diagnostics));
        return (seed, compilation.CreateArrayTypeSymbol(seed.Root!.Type));
    }
}
