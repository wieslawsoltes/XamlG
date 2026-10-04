namespace XamlG.LanguageServer.Tests;

/// <summary>Makes response bytes visible before the shutdown response's final flush completes.</summary>
internal sealed class ShutdownFlushStream : Stream
{
    private readonly object _gate = new();
    private readonly MemoryStream _bytes = new();
    private int _flushes;
    public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FinalFlushStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken FinalFlushCancellation { get; private set; }
    public byte[] Snapshot() { lock (_gate) return _bytes.ToArray(); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _bytes.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _flushes) == 1)
        {
            Initialized.TrySetResult(); return Task.CompletedTask;
        }
        FinalFlushCancellation = cancellationToken;
        FinalFlushStarted.TrySetResult();
        return Release.Task.WaitAsync(cancellationToken);
    }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() { }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { Release.TrySetResult(); _bytes.Dispose(); }
        base.Dispose(disposing);
    }
}
