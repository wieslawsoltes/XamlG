using System.Collections.Immutable;
using System.Globalization;

namespace XamlG.LanguageServer.Diagnostics;

/// <summary>Bounded per-URI diagnostic reports. Unchanged means equal wire values, not merely equal
/// client versions. Unknown, foreign, evicted and oversized results always produce a full report.</summary>
public sealed class LspDiagnosticCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LspDiagnosticCacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly string _identity = Guid.NewGuid().ToString("N");
    private readonly int _capacity;
    private readonly long _maximumCharacters;
    private long _characters;
    private long _sequence;

    public LspDiagnosticCache(int capacity = 256, long maximumCharacters = 4_194_304)
    {
        if (capacity < 1 || maximumCharacters < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity; _maximumCharacters = maximumCharacters;
    }
    public int Count { get { lock (_gate) return _entries.Count; } }
    public long RetainedCharacters { get { lock (_gate) return _characters; } }

    public LspDocumentDiagnosticReport Report(string uri, ImmutableArray<LspDiagnosticItem> items,
        string? previousResultId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (items.IsDefault) items = ImmutableArray<LspDiagnosticItem>.Empty;
        long cost = uri.Length;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cost = checked(cost + item.Code.Length + item.Source.Length + item.Message.Length + 64L);
        }
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.TryGetValue(uri, out var current) && current.Items.SequenceEqual(items))
            {
                _lru.Remove(current.Node); _lru.AddLast(current.Node);
                return current.ResultId == previousResultId
                    ? new(LspDiagnosticMethods.Unchanged, current.ResultId)
                    : new(LspDiagnosticMethods.Full, current.ResultId) { Items = current.Items };
            }
            RemoveCore(uri);
            // Oversized reports are returned in full but are never retained and advertise no cache token.
            if (cost > _maximumCharacters) return new(LspDiagnosticMethods.Full, null) { Items = items };
            while (_entries.Count >= _capacity || cost > _maximumCharacters - _characters) RemoveCore(_lru.First!.Value);
            var resultId = _identity + ":" + checked(++_sequence).ToString(CultureInfo.InvariantCulture);
            var entry = new LspDiagnosticCacheEntry(resultId, items, cost, _lru.AddLast(uri));
            _entries.Add(uri, entry); _characters += cost;
            return new(LspDiagnosticMethods.Full, resultId) { Items = items };
        }
    }
    public void Remove(string uri) { lock (_gate) RemoveCore(uri); }
    public void Clear() { lock (_gate) { _entries.Clear(); _lru.Clear(); _characters = 0; } }
    private void RemoveCore(string uri)
    {
        if (!_entries.Remove(uri, out var entry)) return;
        _lru.Remove(entry.Node); _characters -= entry.Characters;
    }
}
