using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace XamlG.LanguageServer.Diagnostics;

/// <summary>Debounces refresh requests and permits one outstanding request plus one coalesced signal.
/// Only replies to the exact currently owned ID are accepted. Missing replies expire instead of leaking waiters.</summary>
public sealed class LspDiagnosticRefreshQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Func<object, CancellationToken, ValueTask> _send;
    private readonly Action<Exception>? _report;
    private readonly CancellationTokenSource _lifetime;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _replyTimeout;
    private readonly string _prefix = "xamlg-diagnostics/" + Guid.NewGuid().ToString("N") + "/";
    private readonly Task _pump;
    private string? _pendingId;
    private TaskCompletionSource? _pending;
    private long _sequence;
    private bool _disposed;
    private int _sourceDisposed;

    public LspDiagnosticRefreshQueue(Func<object, CancellationToken, ValueTask> send, CancellationToken lifetime = default,
        TimeSpan? debounce = null, TimeSpan? replyTimeout = null, Action<Exception>? report = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(150); _replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(10);
        if (_debounce < TimeSpan.Zero || _debounce > TimeSpan.FromSeconds(5) || _replyTimeout <= TimeSpan.Zero || _replyTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(debounce));
        _report = report; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _pump = Task.Run(PumpAsync);
    }
    public void Signal() { lock (_gate) if (!_disposed) _signals.Writer.TryWrite(0); }
    public bool AcceptResponse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !(response.TryGetProperty("result", out _) ^ response.TryGetProperty("error", out _))) return false;
        lock (_gate)
        {
            if (_pendingId == null || id.GetString() != _pendingId) return false;
            _pending!.TrySetResult(); return true;
        }
    }
    private async Task PumpAsync()
    {
        try
        {
            while (await _signals.Reader.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                while (_signals.Reader.TryRead(out _)) { }
                if (_debounce != TimeSpan.Zero)
                    do { await Task.Delay(_debounce, _lifetime.Token).ConfigureAwait(false); } while (_signals.Reader.TryRead(out _));
                string id; TaskCompletionSource completion;
                lock (_gate)
                {
                    if (_disposed) return;
                    id = _prefix + checked(++_sequence).ToString(CultureInfo.InvariantCulture);
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingId = id; _pending = completion;
                }
                try
                {
                    await _send(new { jsonrpc = "2.0", id, method = LspDiagnosticMethods.Refresh }, _lifetime.Token).ConfigureAwait(false);
                    await completion.Task.WaitAsync(_replyTimeout, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception error) { try { _report?.Invoke(error); } catch { } }
                finally { lock (_gate) if (_pendingId == id) { _pendingId = null; _pending = null; } }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed) { _disposed = true; _lifetime.Cancel(); _signals.Writer.TryComplete(); }
        }
        await _pump.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _sourceDisposed, 1) == 0) _lifetime.Dispose();
    }
}
