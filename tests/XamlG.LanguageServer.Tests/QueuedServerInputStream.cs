using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XamlG.LanguageServer.Tests;

/// <summary>In-memory input with deliberately synchronous completion. Awaiting the pending-read
/// barrier and delivering exit runs a context-free server reader through its drain boundary.</summary>
internal sealed class QueuedServerInputStream : Stream
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _messages = new();
    private byte[]? _current;
    private int _offset;
    private Memory<byte> _pendingBuffer;
    private TaskCompletionSource<int>? _pending;
    private TaskCompletionSource _pendingReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _registration;
    private bool _complete;

    public Task WaitForPendingReadAsync() { lock (_gate) return _pending != null ? Task.CompletedTask : _pendingReady.Task; }
    public void Send(object message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes("Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
        lock (_gate) _messages.Enqueue(header.Concat(payload).ToArray());
        Deliver();
    }
    public void Complete()
    {
        lock (_gate) _complete = true;
        Deliver();
    }
    private void Deliver()
    {
        TaskCompletionSource<int>? completion;
        CancellationTokenRegistration registration;
        int count;
        lock (_gate)
        {
            completion = _pending;
            if (completion == null) return;
            count = ReadAvailable(_pendingBuffer);
            if (count < 0) return;
            registration = _registration;
            _pending = null; _pendingBuffer = default; _registration = default;
            _pendingReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        registration.Dispose();
        completion.TrySetResult(count);
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return ValueTask.FromResult(0);
        lock (_gate)
        {
            var count = ReadAvailable(buffer);
            if (count >= 0) return ValueTask.FromResult(count);
            if (_pending != null) throw new InvalidOperationException("The test input permits one outstanding read.");
            _pendingBuffer = buffer;
            // Inline continuations are intentional in this deterministic scheduling fixture.
            _pending = new TaskCompletionSource<int>(TaskCreationOptions.None);
            _registration = cancellationToken.Register(static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(), _pending);
            _pendingReady.TrySetResult();
            return new(_pending.Task);
        }
    }
    private int ReadAvailable(Memory<byte> buffer)
    {
        if (_current == null && _messages.TryDequeue(out var next)) { _current = next; _offset = 0; }
        if (_current == null) return _complete ? 0 : -1;
        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
        if (_offset == _current.Length) _current = null;
        return count;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() { }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { Complete(); _registration.Dispose(); }
        base.Dispose(disposing);
    }
}
