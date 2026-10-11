using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceSnapshotFastPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_resource_reads_allocate_no_bytes(bool cancellable)
    {
        var document = Create(2);
        using var source = new CancellationTokenSource();
        var token = cancellable ? source.Token : default;
        var snapshot = ResourceReferenceCache.Get(document, token);
        for (var i = 0; i < 128; i++) _ = ResourceReferenceCache.Get(document, token);
        var checksum = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2048; i++) checksum += ResourceReferenceCache.Get(document, token).Length;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2048, checksum);
        Assert.Equal(0, bytes);
        Assert.True(snapshot.Equals(ResourceReferenceCache.Get(document, token)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_roots_never_publish_weak_cache_entries(bool cancellable)
    {
        var document = Create(0);
        using var source = new CancellationTokenSource();
        var token = cancellable ? source.Token : default;
        Assert.False(HasEntry(document.Root!));
        var snapshot = ResourceReferenceCache.Get(document, token);
        Assert.False(snapshot.IsDefault);
        Assert.Empty(snapshot);
        Assert.True(snapshot.Equals(ResourceReferenceCache.Get(document, token)));
        Assert.False(HasEntry(document.Root!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Cancelled_cold_and_warm_reads_still_throw_without_poisoning_the_root(int shape)
    {
        var document = Create(shape);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => ResourceReferenceCache.Get(document, source.Token));
        Assert.False(HasEntry(document.Root!));
        var expected = ResourceReferenceOracle.Scan(document, default);
        var snapshot = ResourceReferenceCache.Get(document, default);
        Assert.Equal(expected.ToArray(), snapshot.ToArray());
        Assert.Throws<OperationCanceledException>(() => ResourceReferenceCache.Get(document, source.Token));
        Assert.True(snapshot.Equals(ResourceReferenceCache.Get(document, default)));
    }

    [Fact]
    public void Cold_factory_routes_preserve_descriptor_identity_and_root_isolation()
    {
        var document = Create(2);
        var other = document with { Root = document.Root! with { } };
        using var source = new CancellationTokenSource();
        var expected = Assert.Single(ResourceReferenceOracle.Scan(document, default));
        Assert.Same(expected, Assert.Single(ResourceReferenceCache.Get(document, default)));
        Assert.Same(expected, Assert.Single(ResourceReferenceCache.Get(other, source.Token)));
        Assert.True(HasEntry(document.Root!));
        Assert.True(HasEntry(other.Root!));
        Assert.NotSame(document.Root, other.Root);
    }

    [Fact]
    public void Holding_many_empty_roots_does_not_create_per_root_cache_storage()
    {
        var seed = Create(0);
        var roots = Enumerable.Range(0, 1024).Select(_ => seed.Root! with { }).ToArray();
        foreach (var root in roots)
        {
            Assert.Empty(ResourceReferenceCache.Get(seed with { Root = root }, default));
            Assert.False(HasEntry(root));
        }
        GC.KeepAlive(roots);
    }

    [Fact]
    public void Concurrent_cold_cancelable_and_default_factories_publish_one_snapshot()
    {
        var document = Create(2);
        using var source = new CancellationTokenSource();
        var snapshots = new ImmutableArray<BoundResourceExpression>[128];
        Parallel.For(0, snapshots.Length, i => snapshots[i] = ResourceReferenceCache.Get(document, i % 2 == 0 ? default : source.Token));
        Assert.All(snapshots, snapshot => Assert.True(snapshot.Equals(snapshots[0])));
        Assert.Single(snapshots[0]);
    }

    [Fact]
    public void Completed_snapshot_does_not_retain_the_cold_cancellation_source()
    {
        var document = Create(2);
        var weak = PopulateCancelable(document);
        for (var i = 0; i < 12 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
        Assert.Single(ResourceReferenceCache.Get(document, default));
        GC.KeepAlive(document);
    }

    [Fact]
    public void Rootless_reads_allocate_no_bytes_and_still_observe_cancellation()
    {
        var document = Create(0) with { Root = null };
        for (var i = 0; i < 128; i++) _ = ResourceReferenceCache.Get(document, default);
        var checksum = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2048; i++) checksum += ResourceReferenceCache.Get(document, default).Length;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, checksum);
        Assert.Equal(0, bytes);
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => ResourceReferenceCache.Get(document, source.Token));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PopulateCancelable(BoundDocument document)
    {
        using var source = new CancellationTokenSource();
        _ = ResourceReferenceCache.Get(document, source.Token);
        return new WeakReference(source);
    }

    private static bool HasEntry(BoundObject root)
    {
        var table = typeof(ResourceReferenceCache).GetField("Roots", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        return (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, new object?[] { root, null })!;
    }

    private static BoundDocument Create(int shape)
    {
        var compilation = CompilationFactory.Create("namespace SnapshotCache { public class Node { public Node Child {get;set;} } }");
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:SnapshotCache'/>", "Snapshot.xaml"), compilation);
        Assert.True(document.Success, string.Join("\n", document.Diagnostics));
        if (shape == 0) return document;
        var root = document.Root!;
        var property = (IPropertySymbol)root.Type.GetMembers("Child").Single();
        var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type, property.GetMethod, property.SetMethod, default);
        BoundExpression value = shape == 1 ? new BoundConstantExpression(null, property.Type, default) :
            new BoundResourceExpression(new("xamlg://snapshot/Child.xaml", root.Type, "Child.xaml", "Generated", null), new(2, 3));
        return document with { Root = root with { Assignments = [new BoundSetAssignment(member, value, default)] } };
    }
}
