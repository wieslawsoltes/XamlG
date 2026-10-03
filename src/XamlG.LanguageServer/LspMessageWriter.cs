using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XamlG.LanguageServer;

/// <summary>Serializes publication and writes complete frames. Request cancellation ends at the
/// publication boundary; only transport lifetime/deadline cancellation can interrupt an active frame.</summary>
internal sealed class LspMessageWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Stream _output;
    private readonly int _maximumPayloadBytes;
    private readonly TimeSpan _writeTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closed;
    private readonly CancellationToken _closedToken;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private LspTransportException? _failure;
    private int _disposing;

    public LspMessageWriter(Stream output, int maximumPayloadBytes, TimeSpan writeTimeout, CancellationToken lifetimeToken)
    {
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        if (maximumPayloadBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        if (writeTimeout <= TimeSpan.Zero || writeTimeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(writeTimeout));
        _output = output;
        _maximumPayloadBytes = maximumPayloadBytes;
        _writeTimeout = writeTimeout;
        _closed = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _closedToken = _closed.Token;
    }

    public CancellationToken Closed => _closedToken;
    public LspTransportException? Failure => Volatile.Read(ref _failure);

    public async ValueTask<bool> TryWriteAsync(object message, Func<bool>? shouldPublish, CancellationToken queueCancellation)
    {
        ThrowIfUnavailable();
        queueCancellation.ThrowIfCancellationRequested();
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > _maximumPayloadBytes) throw new InvalidDataException("The LSP response exceeds the configured limit.");
        var header = Encoding.ASCII.GetBytes("Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(queueCancellation, _closedToken);
        await _gate.WaitAsync(queued.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            queueCancellation.ThrowIfCancellationRequested();
            // This predicate is the linearization point. A subsequent invalidation may queue a
            // newer frame but must not truncate a frame already admitted to the output stream.
            if (shouldPublish != null && !shouldPublish()) return false;
            queueCancellation.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closedToken);
            deadline.CancelAfter(_writeTimeout);
            try
            {
                await AwaitIoAsync(_output.WriteAsync(header.AsMemory(), deadline.Token).AsTask(), deadline.Token).ConfigureAwait(false);
                await AwaitIoAsync(_output.WriteAsync(payload.AsMemory(), deadline.Token).AsTask(), deadline.Token).ConfigureAwait(false);
                await AwaitIoAsync(_output.FlushAsync(deadline.Token), deadline.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception error)
            {
                // Even a failed first write can have emitted bytes. Never append another frame.
                var failure = new LspTransportException("LSP frame publication failed; the connection has been closed.", error);
                Interlocked.CompareExchange(ref _failure, failure, null);
                try { _closed.Cancel(); } catch (AggregateException) { }
                throw Failure!;
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task AwaitIoAsync(Task operation, CancellationToken cancellation)
    {
        try { await operation.WaitAsync(cancellation).ConfigureAwait(false); }
        catch
        {
            // Some Stream implementations ignore cancellation. A deadline still closes this
            // transport. The abandoned operation retains its own buffers and is observed; no
            // subsequent writes are allowed, even if the underlying stream completes later.
            if (!operation.IsCompleted)
                _ = operation.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private void ThrowIfUnavailable()
    {
        if (Failure is { } failure) throw new LspTransportException("The LSP connection previously failed.", failure);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        _closedToken.ThrowIfCancellationRequested();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposing, 1);
        try { await _closed.CancelAsync().ConfigureAwait(false); } catch (AggregateException) { }
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _closed.Dispose();
        // Do not dispose the semaphore while cancelled waiters may still be unwinding. It has
        // no native wait handle (AvailableWaitHandle is never used) and is reclaimed with us.
        // Streams belong to the host and are intentionally left open.
    }
}
