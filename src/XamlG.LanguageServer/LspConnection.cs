using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XamlG.LanguageServer;

/// <summary>Bounded Content-Length framing with cancellation-safe, serialized output publication.</summary>
public sealed class LspConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly int _maximumPayloadBytes;
    private readonly byte[] _buffer = new byte[4096];
    private readonly LspMessageWriter _writer;
    private int _offset;
    private int _count;

    public LspConnection(Stream input, Stream output, int maximumPayloadBytes = 8_388_608,
        TimeSpan? writeTimeout = null, CancellationToken lifetimeToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead) throw new ArgumentException("The input stream is not readable.", nameof(input));
        _input = input;
        _maximumPayloadBytes = maximumPayloadBytes;
        _writer = new(output, maximumPayloadBytes, writeTimeout ?? TimeSpan.FromSeconds(30), lifetimeToken);
    }

    public CancellationToken Closed => _writer.Closed;
    public LspTransportException? Failure => _writer.Failure;

    public async ValueTask<JsonDocument?> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Closed);
        var token = read.Token;
        var header = new StringBuilder();
        while (true)
        {
            var value = await ReadByteAsync(token).ConfigureAwait(false);
            if (value < 0)
            {
                if (header.Length == 0) return null;
                throw new EndOfStreamException("The LSP header is incomplete.");
            }
            if (value > 127 || header.Length >= 8192) throw new InvalidDataException("The LSP header is invalid or too large.");
            header.Append((char)value);
            var length = header.Length;
            if (length >= 4 && header[length - 4] == '\r' && header[length - 3] == '\n' && header[length - 2] == '\r' && header[length - 1] == '\n') break;
        }
        int? contentLength = null;
        foreach (var line in header.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new InvalidDataException("Malformed LSP header field.");
            if (!line.AsSpan(0, separator).Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (contentLength != null || !int.TryParse(line.AsSpan(separator + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0 || length > _maximumPayloadBytes)
                throw new InvalidDataException("The LSP Content-Length is invalid, duplicated, or exceeds the configured limit.");
            contentLength = length;
        }
        if (contentLength == null) throw new InvalidDataException("The LSP Content-Length header is missing.");
        var payload = GC.AllocateUninitializedArray<byte>(contentLength.Value);
        var available = Math.Min(_count - _offset, payload.Length);
        _buffer.AsSpan(_offset, available).CopyTo(payload);
        _offset += available;
        await _input.ReadExactlyAsync(payload.AsMemory(available), token).ConfigureAwait(false);
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
    }

    /// <summary>Cancellation applies only until publication begins. Active writes use the connection lifetime and write deadline.</summary>
    public async ValueTask WriteAsync(object message, CancellationToken cancellationToken = default) =>
        _ = await _writer.TryWriteAsync(message, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Rechecks freshness under the output gate before emitting any bytes. False means the frame was discarded.</summary>
    public ValueTask<bool> TryWriteAsync(object message, Func<bool> shouldPublish, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shouldPublish);
        return _writer.TryWriteAsync(message, shouldPublish, cancellationToken);
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_offset == _count)
        {
            _count = await _input.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _offset = 0;
            if (_count == 0) return -1;
        }
        return _buffer[_offset++];
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
