using System.Threading.Channels;

namespace XamlG.Workspaces.Watching;

/// <summary>One active refresh, one coalesced signal and a latest-revision publication barrier.
/// Even a loader that ignores cancellation cannot publish an obsolete result.</summary>
public sealed class LatestRevisionWorker<T> : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Func<long, CancellationToken, Task<T>> _load;
    private readonly Action<RevisionedValue<T>> _publish;
    private readonly Action<Exception>? _report;
    private readonly TimeSpan _debounce;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private CancellationTokenSource? _active;
    private long _requested;
    private long _published;
    private bool _disposed;

    public LatestRevisionWorker(Func<long, CancellationToken, Task<T>> load, Action<RevisionedValue<T>> publish,
        Action<Exception>? report = null, TimeSpan? debounce = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _report = report;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        if (_debounce < TimeSpan.Zero || _debounce > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(debounce));
        _loop = Task.Run(RunAsync);
    }
    public long RequestedRevision => Interlocked.Read(ref _requested);
    public long PublishedRevision => Interlocked.Read(ref _published);

    public void Signal()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            if (_disposed) return;
            _requested = checked(_requested + 1);
            active = _active;
            _signals.Writer.TryWrite(0);
        }
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _signals.Reader.WaitToReadAsync(_lifetime.Token))
            {
                while (_signals.Reader.TryRead(out _)) { }
                if (_debounce != TimeSpan.Zero)
                {
                    do { await Task.Delay(_debounce, _lifetime.Token); }
                    while (_signals.Reader.TryRead(out _));
                }
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                long revision;
                lock (_gate)
                {
                    if (_disposed) break;
                    revision = _requested;
                    _active = cancellation;
                }
                try
                {
                    var result = await _load(revision, cancellation.Token);
                    lock (_gate)
                    {
                        if (_disposed || cancellation.IsCancellationRequested || revision != _requested) continue;
                        // Publication is intentionally synchronous and linearized against Signal.
                        // Consumers must not synchronously wait for this worker from the callback.
                        _publish(new(revision, result));
                        Interlocked.Exchange(ref _published, revision);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception error)
                {
                    bool current;
                    lock (_gate) current = !_disposed && revision == _requested;
                    if (current) Report(error);
                }
                finally
                {
                    lock (_gate) if (ReferenceEquals(_active, cancellation)) _active = null;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void Report(Exception error)
    {
        try { _report?.Invoke(error); }
        catch { /* An error observer cannot terminate the refresh pump. */ }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel();
        _signals.Writer.TryComplete();
        await _loop;
        _lifetime.Dispose();
    }
}
