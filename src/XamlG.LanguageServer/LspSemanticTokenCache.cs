namespace XamlG.LanguageServer;

public sealed record LspSemanticTokensFull(string ResultId, int[] Data);
public sealed record LspSemanticTokensEdit(int Start, int DeleteCount, int[] Data);
public sealed record LspSemanticTokensDelta(string ResultId, LspSemanticTokensEdit[] Edits);

/// <summary>Bounded per-URI token history. Unknown, evicted or foreign result IDs return a full result.
/// A delta is a single splice aligned to complete five-integer token records.</summary>
public sealed class LspSemanticTokenCache
{
    private readonly int _maximumDocuments;
    private readonly int _maximumIntegers;
    public LspSemanticTokenCache(int maximumDocuments = 256, int maximumIntegers = 1_000_000)
    {
        if (maximumDocuments is < 1 or > 16_384) throw new ArgumentOutOfRangeException(nameof(maximumDocuments));
        if (maximumIntegers is < 5 or > 16_000_000) throw new ArgumentOutOfRangeException(nameof(maximumIntegers));
        _maximumDocuments = maximumDocuments; _maximumIntegers = maximumIntegers;
    }
    private sealed record Snapshot(string Id, int[] Data);
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Snapshot>> _documents = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private long _sequence;
    private int _integers;
    public int RetainedIntegers { get { lock (_gate) return _integers; } }
    public object GetResult(string uri, int[] data, string? previousResultId = null)
    {
        ArgumentNullException.ThrowIfNull(uri); ArgumentNullException.ThrowIfNull(data);
        if (data.Length % 5 != 0 || data.Any(n => n < 0)) throw new ArgumentException("Token data must contain non-negative five-integer records.", nameof(data));
        lock (_gate)
        {
            _documents.TryGetValue(uri, out var history);
            var previous = previousResultId == null ? null : history?.FirstOrDefault(s => s.Id == previousResultId);
            var current = history?.LastOrDefault();
            if (current == null || !current.Data.AsSpan().SequenceEqual(data))
            {
                current = new(_instance + ":" + checked(++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture), (int[])data.Clone());
                if (data.Length <= _maximumIntegers)
                {
                    if (history == null) _documents[uri] = history = new();
                    history.Add(current); _integers += current.Data.Length;
                    while (history.Count > 2) { _integers -= history[0].Data.Length; history.RemoveAt(0); }
                    if (_nodes.Remove(uri, out var oldNode)) _lru.Remove(oldNode);
                    _nodes[uri] = _lru.AddLast(uri);
                    while (_documents.Count > _maximumDocuments || _integers > _maximumIntegers) RemoveCore(_lru.First!.Value);
                }
                else RemoveCore(uri);
            }
            else if (_nodes.TryGetValue(uri, out var hit)) { _lru.Remove(hit); _lru.AddLast(hit); }
            if (previous == null) return new LspSemanticTokensFull(current.Id, (int[])current.Data.Clone());
            var prefix = 0; var shared = Math.Min(previous.Data.Length, current.Data.Length);
            while (prefix < shared && previous.Data.AsSpan(prefix, 5).SequenceEqual(current.Data.AsSpan(prefix, 5))) prefix += 5;
            var suffix = 0;
            while (suffix < shared - prefix && previous.Data.AsSpan(previous.Data.Length - suffix - 5, 5).SequenceEqual(current.Data.AsSpan(current.Data.Length - suffix - 5, 5))) suffix += 5;
            if (prefix == previous.Data.Length && prefix == current.Data.Length) return new LspSemanticTokensDelta(current.Id, Array.Empty<LspSemanticTokensEdit>());
            return new LspSemanticTokensDelta(current.Id, new[] { new LspSemanticTokensEdit(prefix, previous.Data.Length - prefix - suffix,
                current.Data.AsSpan(prefix, current.Data.Length - prefix - suffix).ToArray()) });
        }
    }
    public void Remove(string uri) { lock (_gate) RemoveCore(uri); }
    private void RemoveCore(string uri)
    {
        if (_documents.Remove(uri, out var history)) _integers -= history.Sum(s => s.Data.Length);
        if (_nodes.Remove(uri, out var node)) _lru.Remove(node);
    }
}
