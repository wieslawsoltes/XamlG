using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XamlG.LanguageServer;

/// <summary>Bounded Content-Length framing. Buffered reads handle fragmented headers and multiple messages per read.</summary>
public sealed class LspConnection(Stream input, Stream output, int maximumPayloadBytes = 8_388_608) : IAsyncDisposable
{
    private readonly byte[] _buffer = new byte[4096];
    private readonly SemaphoreSlim _writeGate = new(1);
    private int _offset;
    private int _count;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<JsonDocument?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var header = new StringBuilder();
        while (true)
        {
            var value = await ReadByteAsync(cancellationToken);
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
            if (contentLength != null || !int.TryParse(line.AsSpan(separator + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0 || length > maximumPayloadBytes)
                throw new InvalidDataException("The LSP Content-Length is invalid, duplicated, or exceeds the configured limit.");
            contentLength = length;
        }
        if (contentLength == null) throw new InvalidDataException("The LSP Content-Length header is missing.");
        var payload = GC.AllocateUninitializedArray<byte>(contentLength.Value);
        var available = Math.Min(_count - _offset, payload.Length);
        _buffer.AsSpan(_offset, available).CopyTo(payload);
        _offset += available;
        await input.ReadExactlyAsync(payload.AsMemory(available), cancellationToken);
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
    }

    public async ValueTask WriteAsync(object message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > maximumPayloadBytes) throw new InvalidDataException("The LSP response exceeds the configured limit.");
        var header = Encoding.ASCII.GetBytes("Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(payload, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_offset == _count)
        {
            _count = await input.ReadAsync(_buffer, cancellationToken);
            _offset = 0;
            if (_count == 0) return -1;
        }
        return _buffer[_offset++];
    }

    public ValueTask DisposeAsync() { _writeGate.Dispose(); return ValueTask.CompletedTask; }
}
