namespace XamlG.LanguageServer.Tests;

/// <summary>Deterministically pauses or fails a particular write after recording its bytes.</summary>
internal sealed class ControlledOutputStream : Stream
{
    private readonly MemoryStream _bytes = new();
    private readonly object _gate = new();
    private int _writes;
    public int BlockWrite { get; init; } = 1;
    public int FailWrite { get; init; }
    public bool IgnoreCancellation { get; init; }
    public bool FailFlush { get; init; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int WriteCalls => Volatile.Read(ref _writes);
    public byte[] Snapshot() { lock (_gate) return _bytes.ToArray(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _writes);
        lock (_gate) _bytes.Write(buffer.Span);
        if (call == FailWrite) throw new IOException("Injected partial-write failure.");
        if (call == BlockWrite)
        {
            Started.TrySetResult();
            if (IgnoreCancellation) await Release.Task;
            else await Release.Task.WaitAsync(cancellationToken);
        }
    }
    public override Task FlushAsync(CancellationToken cancellationToken) => FailFlush
        ? Task.FromException(new IOException("Injected flush failure.")) : Task.CompletedTask;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    protected override void Dispose(bool disposing) { if (disposing) _bytes.Dispose(); base.Dispose(disposing); }
}
