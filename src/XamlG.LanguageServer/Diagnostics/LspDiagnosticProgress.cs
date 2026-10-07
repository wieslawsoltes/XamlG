using System.Text.Json;

namespace XamlG.LanguageServer.Diagnostics;

/// <summary>Request-owned, backpressured partial results. Never splits a document's replacement
/// diagnostic list. Each publication is awaited before projecting another batch.</summary>
internal sealed class LspDiagnosticProgress
{
    internal const int MaximumBatchDocuments = 32;
    internal const int TargetPayloadBytes = 262_144;
    internal const int MaximumTokenCharacters = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly JsonElement _token;
    private readonly Func<object, CancellationToken, ValueTask> _publish;

    internal LspDiagnosticProgress(JsonElement token, Func<object, CancellationToken, ValueTask> publish)
    {
        _token = token.Clone();
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    internal static JsonElement? ReadToken(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new LspRequestException(-32602, "Diagnostic parameters must be an object.");
        if (!parameters.TryGetProperty("partialResultToken", out var token)) return null;
        // ProgressToken is an LSP integer or string. Empty strings and zero are valid;
        // null, fractions, booleans, containers and out-of-range integers are not.
        if (token.ValueKind == JsonValueKind.String && token.GetString()!.Length <= MaximumTokenCharacters ||
            token.ValueKind == JsonValueKind.Number && token.TryGetInt32(out _)) return token.Clone();
        throw new LspRequestException(-32602, "partialResultToken must be an LSP integer or a string of at most 1024 characters.");
    }

    internal ValueTask WriteAsync(object value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _publish(Message(value), cancellationToken);
    }

    internal Task WriteWorkspaceAsync(IEnumerable<LspWorkspaceDocumentDiagnosticReport> reports, CancellationToken token) =>
        WriteBatchesAsync(reports, Size, batch => new { items = batch }, token);

    internal Task WriteRelatedAsync(IEnumerable<KeyValuePair<string, LspDocumentDiagnosticReport>> reports, CancellationToken token) =>
        WriteBatchesAsync(reports, pair => checked(Size(pair.Key) + 1 + Size(pair.Value)),
            batch => new { relatedDocuments = batch.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) }, token);

    private object Message(object value) => new { jsonrpc = "2.0", method = "$/progress", @params = new { token = _token, value } };
    private static int Size<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;

    private async Task WriteBatchesAsync<T>(IEnumerable<T> reports, Func<T, int> size,
        Func<T[], object> value, CancellationToken token)
    {
        // The envelope includes the escaped token, field names and empty collection delimiters.
        // Entry sizes plus commas give actual serialized UTF-8 bytes, not UTF-16 character estimates.
        var envelopeBytes = Size(Message(value(Array.Empty<T>())));
        var batch = new List<T>(MaximumBatchDocuments);
        long bytes = envelopeBytes;
        using var enumerator = reports.GetEnumerator();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext()) break;
            var report = enumerator.Current;
            var reportBytes = size(report);
            if (batch.Count != 0 && bytes + 1L + reportBytes > TargetPayloadBytes)
            {
                await WriteAsync(value(batch.ToArray()), token).ConfigureAwait(false);
                batch.Clear(); bytes = envelopeBytes;
            }
            if (batch.Count != 0) bytes++;
            batch.Add(report); bytes += reportBytes;
            if (batch.Count == MaximumBatchDocuments || bytes >= TargetPayloadBytes)
            {
                // A single report may exceed the batching target: send it intact. The connection's
                // hard payload limit still applies; partial lists would incorrectly clear diagnostics.
                await WriteAsync(value(batch.ToArray()), token).ConfigureAwait(false);
                batch.Clear(); bytes = envelopeBytes;
            }
        }
        if (batch.Count != 0) await WriteAsync(value(batch.ToArray()), token).ConfigureAwait(false);
    }
}
