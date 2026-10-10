using System.Runtime.CompilerServices;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class SharedRuntimeAccessorTests
{
    private sealed class Box { public int Value; }
    private static XamlPropertyTable Table() => new(new[] { typeof(int) }, new[] { "Value" },
        static (target, _) => ((Box)target).Value, static (target, _, value) => ((Box)target).Value = (int)value!);

    [Fact]
    public void Typed_and_delegate_registrations_replace_each_other_without_stale_targets()
    {
        var table = Table(); var box = new Box(); var delegated = 0;
        using var session = new XamlRuntimeSession();
        table.Register(session, "node", "Value", box, 0);
        session.RegisterProperty("node", "Value", () => delegated, value => delegated = value);
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate("node", "Value", 3) }).Applied);
        Assert.Equal(3, delegated); Assert.Equal(0, box.Value);
        table.Register(session, "node", "Value", box, 0);
        Assert.True(session.Apply(1, new[] { new XamlPropertyUpdate("node", "Value", 4) }).Applied);
        Assert.Equal(3, delegated); Assert.Equal(4, box.Value);
    }

    [Fact]
    public void Pending_mutations_capture_targets_even_when_setters_replace_registrations()
    {
        var first = new Box(); var oldTarget = new Box(); var nextTarget = new Box();
        using var session = new XamlRuntimeSession();
        XamlPropertyTable? table = null;
        table = new(new[] { typeof(int) }, static (target, _) => ((Box)target).Value, (target, _, value) =>
        {
            ((Box)target).Value = (int)value!;
            if (ReferenceEquals(target, first)) table!.Register(session, "second", "Value", nextTarget, 0);
        });
        table.Register(session, "first", "Value", first, 0);
        table.Register(session, "second", "Value", oldTarget, 0);
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate("first", "Value", 10), new XamlPropertyUpdate("second", "Value", 20) }).Applied);
        Assert.Equal(20, oldTarget.Value); Assert.Equal(0, nextTarget.Value);
        Assert.True(session.Apply(1, new[] { new XamlPropertyUpdate("second", "Value", 30) }).Applied);
        Assert.Equal(20, oldTarget.Value); Assert.Equal(30, nextTarget.Value);
    }

    [Fact]
    public void Lazy_accessor_publication_is_thread_safe_but_sessions_remain_thread_affine()
    {
        var table = Table();
        Parallel.For(0, 64, i =>
        {
            var box = new Box();
            using var session = new XamlRuntimeSession();
            table.Register(session, "node", "Value", box, 0);
            Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate("node", "Value", i) }).Applied);
            Assert.Equal(i, box.Value);
        });
    }

    [Fact]
    public void Invalid_slots_keep_their_original_exception_order_even_after_disposal()
    {
        var table = Table(); var session = new XamlRuntimeSession(); session.Dispose();
        Assert.Equal("session", Assert.Throws<ArgumentNullException>(() => table.Register(null!, "node", "Value", new Box(), 99)).ParamName);
        Assert.Throws<IndexOutOfRangeException>(() => table.Register(session, "node", "Value", new Box(), -1));
        Assert.Throws<ObjectDisposedException>(() => table.Register(session, "node", "Value", new Box(), 0));
    }

    [Fact]
    public void Shared_metadata_does_not_retain_session_targets()
    {
        var table = Table();
        var target = RegisterAndDispose(table);
        for (var i = 0; i < 5 && target.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(target.IsAlive);
        GC.KeepAlive(table);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAndDispose(XamlPropertyTable table)
    {
        var box = new Box();
        using var session = new XamlRuntimeSession();
        table.Register(session, "node", "Value", box, 0);
        return new(box);
    }
}
