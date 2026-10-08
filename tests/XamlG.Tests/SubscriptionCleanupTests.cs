using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class SubscriptionCleanupTests
{
    [Fact]
    public void ActionsAndSubscriptionsRetireInReverseRegistrationOrderOnlyOnce()
    {
        var log = new List<int>();
        var session = new XamlRuntimeSession();
        session.TrackDisposable(new Subscription(() => log.Add(1)));
        session.TrackCleanup(() => log.Add(2));
        session.TrackDisposable(null);
        session.TrackDisposable(new Subscription(() => log.Add(3)));
        session.TrackCleanup(() => { log.Add(4); session.Dispose(); });
        session.Dispose();
        session.Dispose();
        Assert.Equal(new[] { 4, 3, 2, 1 }, log);
    }

    [Fact]
    public void AllCleanupRunsEvenWhenActionsAndSubscriptionsThrow()
    {
        var log = new List<int>();
        var session = new XamlRuntimeSession();
        session.TrackDisposable(new Subscription(() => log.Add(1)));
        session.TrackCleanup(() => throw new InvalidOperationException("action"));
        session.TrackDisposable(new Subscription(() => throw new InvalidOperationException("subscription")));
        var error = Assert.Throws<AggregateException>(session.Dispose);
        Assert.Equal(new[] { "subscription", "action" }, error.InnerExceptions.Select(e => e.Message));
        Assert.Equal(new[] { 1 }, log);
        session.Dispose();
    }

    [Fact]
    public void SubscriptionRegistrationRequiresTheOwningThreadAndALiveSession()
    {
        var session = new XamlRuntimeSession();
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(() => session.TrackDisposable(null)));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(error);
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.TrackDisposable(null));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
