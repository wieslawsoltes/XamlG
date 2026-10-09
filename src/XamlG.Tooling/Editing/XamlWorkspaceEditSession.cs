using System.Collections.Immutable;
using System.Text;
using XamlG.Tooling.Refactoring;

namespace XamlG.Tooling.Editing;

/// <summary>Atomic optimistic source transactions across XAML and C# with bounded project undo/redo.
/// No file writes or runtime execution. Plans validate every original buffer before publishing any edit.</summary>
public sealed class XamlWorkspaceEditSession
{
    private readonly object _gate = new();
    private readonly int _maximumDocuments;
    private readonly int _maximumCharacters;
    private readonly int _historyCapacity;
    private readonly long _historyCharacters;
    private readonly LinkedList<XamlWorkspaceHistoryEntry> _undo = new();
    private readonly Stack<XamlWorkspaceHistoryEntry> _redo = new();
    private XamlWorkspaceSnapshot _current;
    private long _retainedCharacters;

    public XamlWorkspaceEditSession(IEnumerable<KeyValuePair<string, string>> documents,
        int maximumDocuments = 256, int maximumCharacters = 8_388_608,
        int historyCapacity = 128, long historyCharacters = 67_108_864)
    {
        if (maximumDocuments < 1 || maximumCharacters < 1 || historyCapacity < 1 || historyCharacters < 2L * maximumCharacters)
            throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        _maximumDocuments = maximumDocuments; _maximumCharacters = maximumCharacters;
        _historyCapacity = historyCapacity; _historyCharacters = historyCharacters;
        _current = new(0, ValidateDocuments(documents));
    }
    public XamlWorkspaceSnapshot Current { get { lock (_gate) return _current; } }
    public bool CanUndo { get { lock (_gate) return _undo.Count != 0; } }
    public bool CanRedo { get { lock (_gate) return _redo.Count != 0; } }
    public string? UndoDescription { get { lock (_gate) return _undo.Last?.Value.Description; } }
    public string? RedoDescription { get { lock (_gate) return _redo.Count == 0 ? null : _redo.Peek().Description; } }

    public XamlWorkspaceSavedState CaptureState()
    { lock (_gate) return new(1, _current, _undo.ToArray(), _redo.ToArray()); }

    public XamlWorkspaceSnapshot RestoreState(XamlWorkspaceSavedState saved)
    {
        if (saved.Version != 1 || saved.Current.Revision < 0 || saved.Undo.Count + saved.Redo.Count > _historyCapacity)
            throw new ArgumentException("Invalid saved workspace history.");
        var current = ValidateDocuments(saved.Current.Documents);
        var cost = 0L;
        foreach (var entry in saved.Undo.Concat(saved.Redo))
        {
            ValidateDocuments(entry.Before); ValidateDocuments(entry.After);
            if (string.IsNullOrWhiteSpace(entry.Description) || entry.Characters != CharacterCount(entry.Before) + CharacterCount(entry.After))
                throw new ArgumentException("Invalid saved workspace entry.");
            cost = checked(cost + entry.Characters);
        }
        if (cost > _historyCharacters || saved.Undo.Count > 0 && !Same(saved.Undo.Last().After, current) || saved.Redo.Count > 0 && !Same(saved.Redo.First().Before, current))
            throw new ArgumentException("Saved history does not match its source snapshot.");
        lock (_gate)
        {
            _undo.Clear(); foreach (var entry in saved.Undo) _undo.AddLast(entry);
            _redo.Clear(); foreach (var entry in saved.Redo.Reverse()) _redo.Push(entry);
            _retainedCharacters = cost;
            return _current = new(Math.Max(_current.Revision + 1, saved.Current.Revision), current);
        }
    }

    public XamlWorkspaceSnapshot ReplaceAll(long expectedRevision, IEnumerable<KeyValuePair<string, string>> documents,
        string description, bool recordHistory = true)
    {
        var replacement = ValidateDocuments(documents);
        lock (_gate)
        {
            CheckRevision(expectedRevision);
            return Publish(replacement, description, recordHistory);
        }
    }

    public XamlWorkspaceSnapshot Apply(long expectedRevision, IEnumerable<XamlDocumentEdits> edits,
        string description, Action<XamlWorkspaceSnapshot>? validate = null)
    {
        if (edits == null) throw new ArgumentNullException(nameof(edits));
        XamlWorkspaceSnapshot previous, candidate;
        lock (_gate)
        {
            CheckRevision(expectedRevision); previous = _current;
            var builder = previous.Documents.ToBuilder();
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var document in edits)
            {
                if (!changed.Add(document.Path)) throw new InvalidOperationException("The transaction contains duplicate document edits.");
                if (!previous.Documents.TryGetValue(document.Path, out var text) || text != document.OriginalText)
                    throw new InvalidOperationException("A source buffer changed since this edit was planned: " + document.Path);
                var replacements = document.Changes.OrderBy(c => c.Span.Start).ToArray();
                var end = 0; var previousStart = -1;
                var output = new StringBuilder();
                foreach (var replacement in replacements)
                {
                    var span = replacement.Span;
                    if (span.Start < end || span.Start == previousStart || span.Start < 0 || span.Length < 0 ||
                        span.Start > text.Length || span.Length > text.Length - span.Start || replacement.NewText == null ||
                        SplitsPair(text, span.Start) || SplitsPair(text, span.Start + span.Length))
                        throw new InvalidOperationException("Conflicting or invalid UTF-16 source edit in " + document.Path);
                    output.Append(text, end, span.Start - end); output.Append(replacement.NewText);
                    if (output.Length > _maximumCharacters) throw new InvalidOperationException("The source edit exceeds the character budget.");
                    previousStart = span.Start; end = span.Start + span.Length;
                }
                output.Append(text, end, text.Length - end);
                builder[document.Path] = output.ToString();
            }
            candidate = new(checked(previous.Revision + 1), ValidateDocuments(builder));
        }
        // A host may parse/type-check without running under the edit gate. A concurrent edit
        // or reentrant validator invalidates publication rather than losing either buffer.
        validate?.Invoke(candidate);
        lock (_gate)
        {
            CheckRevision(expectedRevision);
            return Publish(candidate.Documents, description, true);
        }
    }

    public XamlWorkspaceSnapshot Undo(long expectedRevision)
    {
        lock (_gate)
        {
            CheckRevision(expectedRevision);
            if (_undo.Last == null) return _current;
            var entry = _undo.Last.Value;
            var result = new XamlWorkspaceSnapshot(checked(_current.Revision + 1), entry.Before);
            _undo.RemoveLast(); _redo.Push(entry); _current = result;
            return result;
        }
    }
    public XamlWorkspaceSnapshot Redo(long expectedRevision)
    {
        lock (_gate)
        {
            CheckRevision(expectedRevision);
            if (_redo.Count == 0) return _current;
            var entry = _redo.Peek();
            var result = new XamlWorkspaceSnapshot(checked(_current.Revision + 1), entry.After);
            _redo.Pop(); _undo.AddLast(entry); _current = result;
            return result;
        }
    }
    /// <summary>Inspect a history destination without consuming Undo/Redo or publishing source.</summary>
    public XamlWorkspaceSnapshot PreviewHistory(long expectedRevision, bool undo)
    {
        lock (_gate)
        {
            CheckRevision(expectedRevision);
            return new(_current.Revision, undo ? _undo.Last?.Value.Before ?? _current.Documents : _redo.Count == 0 ? _current.Documents : _redo.Peek().After);
        }
    }
    public void ClearHistory()
    {
        lock (_gate) { _undo.Clear(); _redo.Clear(); _retainedCharacters = 0; }
    }
    private XamlWorkspaceSnapshot Publish(ImmutableDictionary<string, string> replacement, string description, bool record)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("An edit description is required.", nameof(description));
        if (Same(_current.Documents, replacement)) return _current;
        var next = new XamlWorkspaceSnapshot(checked(_current.Revision + 1), replacement);
        if (record)
        {
            foreach (var discarded in _redo) _retainedCharacters -= discarded.Characters;
            _redo.Clear();
            var cost = CharacterCount(_current.Documents) + CharacterCount(replacement);
            _undo.AddLast(new XamlWorkspaceHistoryEntry(description, _current.Documents, replacement, cost));
            _retainedCharacters += cost;
            while (_undo.Count > _historyCapacity || _retainedCharacters > _historyCharacters)
            { _retainedCharacters -= _undo.First!.Value.Characters; _undo.RemoveFirst(); }
        }
        else { _undo.Clear(); _redo.Clear(); _retainedCharacters = 0; }
        return _current = next;
    }
    private ImmutableDictionary<string, string> ValidateDocuments(IEnumerable<KeyValuePair<string, string>> documents)
    {
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        long characters = 0;
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.Key) || document.Key.Length > 4096 || document.Key.Any(char.IsControl) || document.Value == null)
                throw new ArgumentException("Every source document needs a nonempty path and text.", nameof(documents));
            if (result.ContainsKey(document.Key)) throw new ArgumentException("Duplicate source path: " + document.Key, nameof(documents));
            characters += document.Value.Length;
            if (result.Count >= _maximumDocuments || characters > _maximumCharacters)
                throw new InvalidOperationException("The workspace source budget would be exceeded.");
            result.Add(document.Key, document.Value);
        }
        return result.ToImmutable();
    }
    private void CheckRevision(long expected)
    {
        if (_current.Revision != expected) throw new InvalidOperationException("The workspace edit belongs to a stale project revision.");
    }
    private static bool Same(ImmutableDictionary<string, string> left, ImmutableDictionary<string, string> right) =>
        left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out var value) && value == p.Value);
    private static long CharacterCount(ImmutableDictionary<string, string> documents) => documents.Sum(p => (long)p.Value.Length);
    private static bool SplitsPair(string text, int offset) => offset > 0 && offset < text.Length &&
        char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);
}
