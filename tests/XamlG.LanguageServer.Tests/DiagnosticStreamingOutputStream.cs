using System.Text.Json;
using System.Threading.Channels;

namespace XamlG.LanguageServer.Tests;

/// <summary>Records actual frames and pauses the first admitted progress payload. A request
/// cancellation must not cancel this write: its token belongs to the transport lifetime.</summary>
internal sealed class DiagnosticStreamingOutputStream : Stream
{
    private readonly MemoryStream _bytes = new();
    private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
    private int _progress;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken AdmittedCancellation { get; private set; }
    public Task<JsonElement> ReadMessageAsync() => _messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    public byte[] Snapshot() { lock (_bytes) return _bytes.ToArray(); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bytes) _bytes.Write(buffer.Span);
        if (buffer.IsEmpty || buffer.Span[0] != (byte)'{') return;
        using var parsed = JsonDocument.Parse(buffer);
        var message = parsed.RootElement.Clone();
        var block = message.TryGetProperty("method", out var method) && method.GetString() == "$/progress" && ++_progress == 1;
        if (block) AdmittedCancellation = cancellationToken;
        await _messages.Writer.WriteAsync(message, cancellationToken);
        if (block)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
    public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
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
        if (disposing) { Release.TrySetResult(); _messages.Writer.TryComplete(); _bytes.Dispose(); }
        base.Dispose(disposing);
    }
}
