using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;

namespace XamlG.Agents;

/// <summary>Check error frames before an SDK converter can discard unknown fields.
/// The SDK still deserializes normal responses and owns native continuation models.</summary>
internal sealed class AgentEventStreamContent : HttpContent
{
    private readonly HttpContent _source;
    internal AgentEventStreamContent(HttpContent source)
    {
        _source = source;
        foreach (var header in source.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }
    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        new ErrorCheckingStream(await _source.ReadAsStreamAsync(cancellationToken), cancellationToken);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var guarded = await CreateContentReadStreamAsync(cancellationToken);
        await guarded.CopyToAsync(stream, cancellationToken);
    }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override void Dispose(bool disposing) { if (disposing) _source.Dispose(); base.Dispose(disposing); }

    private sealed class ErrorCheckingStream : ReadOnlyStream
    {
        private readonly BoundedStream _source;
        private readonly CancellationTokenSource _lifetime;
        private readonly IAsyncEnumerator<SseItem<string>> _events;
        private byte[] _frame = [];
        private int _offset;
        private bool _disposed;
        internal ErrorCheckingStream(Stream source, CancellationToken token)
        {
            _source = new(source); _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _events = SseParser.Create(_source).EnumerateAsync(_lifetime.Token).GetAsyncEnumerator(_lifetime.Token);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.Length == 0) return 0;
            using var cancellation = cancellationToken.Register(_lifetime.Cancel);
            if (_offset == _frame.Length)
            {
                if (!await _events.MoveNextAsync()) return 0;
                var item = _events.Current;
                Inspect(item);
                // Return one event at a time. A later error must not hide usage from a
                // preceding event that arrived in the same network read.
                var prefix = item.EventType == "message" ? "" : "event: " + item.EventType + "\n";
                _frame = Encoding.UTF8.GetBytes(prefix + "data: " + item.Data.Replace("\n", "\ndata: ", StringComparison.Ordinal) + "\n\n");
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _frame.Length - _offset);
            _frame.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        private static void Inspect(SseItem<string> item)
        {
            try
            {
                using var document = JsonDocument.Parse(item.Data, new JsonDocumentOptions { MaxDepth = 64 });
                var data = document.RootElement;
                if (data.ValueKind != JsonValueKind.Object) return;
                var hasError = data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
                if (!hasError && item.EventType != "error" && !(data.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "error")) return;
                var status = hasError && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) && number is >= 400 and <= 599 ? number : 0;
                throw AgentProviderErrors.FromJson(data, status);
            }
            catch (JsonException)
            {
                // [DONE] and provider-specific payloads remain the SDK's responsibility.
                if (item.EventType == "error") throw AgentProviderErrors.FromCode(null);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true; _lifetime.Cancel();
                try { _events.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                finally { _source.Dispose(); _lifetime.Dispose(); _frame = []; }
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true; _lifetime.Cancel();
                try { await _events.DisposeAsync(); }
                finally { await _source.DisposeAsync(); _lifetime.Dispose(); _frame = []; }
            }
            GC.SuppressFinalize(this);
        }
    }

    private sealed class BoundedStream(Stream source) : ReadOnlyStream
    {
        private long _bytes;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            _bytes += count;
            if (_bytes > 16 * 1024 * 1024) throw new AgentProviderException("response_too_large", false, canResume: false);
            return count;
        }
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => source.DisposeAsync();
    }
    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
