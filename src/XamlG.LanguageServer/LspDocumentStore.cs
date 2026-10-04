using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using XamlG.Syntax;

namespace XamlG.LanguageServer;

/// <summary>Atomic UTF-16 document edits and coherent open-buffer snapshots. Each edit batch is sequential.</summary>
public sealed class LspDocumentStore(int maximumDocuments = 256, int maximumCharacters = 4_194_304)
{
    private readonly object _gate = new();
    private ImmutableDictionary<string, LspDocumentSnapshot> _documents = ImmutableDictionary<string, LspDocumentSnapshot>.Empty.WithComparers(StringComparer.Ordinal);
    private ImmutableDictionary<string, LspCSharpDocumentSnapshot> _csharp = ImmutableDictionary<string, LspCSharpDocumentSnapshot>.Empty.WithComparers(StringComparer.Ordinal);
    private long _revision;
    public ImmutableArray<LspDocumentSnapshot> Snapshots => Capture().Documents;
    public LspDocumentSetSnapshot Capture() { lock (_gate) return new(_revision, _documents.Values.ToImmutableArray()) { CSharpDocuments = _csharp.Values.ToImmutableArray() }; }
    public bool IsCurrent(LspDocumentSetSnapshot snapshot) { lock (_gate) return snapshot.Revision == _revision; }

    public LspDocumentSnapshot Open(string uri, int version, string text)
    {
        ValidateSize(text); var path = DocumentPath(uri);
        lock (_gate)
        {
            if (_documents.ContainsKey(uri) || _csharp.ContainsKey(uri)) throw new LspRequestException(-32602, "The document is already open.");
            var comparer = OperatingSystem.IsWindows() && uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (_documents.Values.Any(d => comparer.Equals(d.Syntax.Path, path)) || _csharp.Values.Any(d => comparer.Equals(d.Path, path))) throw new LspRequestException(-32602, "The same document is already open under another URI.");
            if (_documents.Count + _csharp.Count >= maximumDocuments) throw new LspRequestException(-32602, "The open-document limit has been reached.");
            var nextRevision = checked(_revision + 1);
            var snapshot = new LspDocumentSnapshot(uri, version, XamlSyntaxTree.Parse(text, path, version: version));
            _documents = _documents.Add(uri, snapshot); _revision = nextRevision;
            return snapshot;
        }
    }
    public LspDocumentSnapshot Change(string uri, int version, IReadOnlyList<LspTextChange> changes)
    {
        lock (_gate)
        {
            var previous = GetCore(uri);
            if (version <= previous.Version) throw new LspRequestException(-32801, "The document change has a stale version.");
            var text = ApplyChanges(SourceText.From(previous.Syntax.Text), changes);
            var nextRevision = checked(_revision + 1);
            var snapshot = new LspDocumentSnapshot(uri, version, XamlSyntaxTree.Parse(text.ToString(), previous.Syntax.Path, options: previous.Syntax.Options, version: version));
            _documents = _documents.SetItem(uri, snapshot); _revision = nextRevision;
            return snapshot;
        }
    }
    public bool IsCSharp(string uri) { lock (_gate) return _csharp.ContainsKey(uri); }
    public LspCSharpDocumentSnapshot OpenCSharp(string uri, int version, string text)
    {
        ValidateSize(text); var path = DocumentPath(uri);
        lock (_gate)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (_documents.Values.Any(d => comparer.Equals(d.Syntax.Path, path)) || _csharp.Values.Any(d => comparer.Equals(d.Path, path)))
                throw new LspRequestException(-32602, "The document is already open, possibly under another URI.");
            if (_documents.Count + _csharp.Count >= maximumDocuments) throw new LspRequestException(-32602, "The open-document limit has been reached.");
            var next = checked(_revision + 1);
            var snapshot = new LspCSharpDocumentSnapshot(uri, path, version, SourceText.From(text));
            _csharp = _csharp.Add(uri, snapshot); _revision = next;
            return snapshot;
        }
    }
    public LspCSharpDocumentSnapshot ChangeCSharp(string uri, int version, IReadOnlyList<LspTextChange> changes)
    {
        lock (_gate)
        {
            if (!_csharp.TryGetValue(uri, out var previous)) throw new LspRequestException(-32602, "The C# document is not open.");
            if (version <= previous.Version) throw new LspRequestException(-32801, "The C# change has a stale version.");
            var text = ApplyChanges(previous.Text, changes);
            var next = checked(_revision + 1);
            var snapshot = previous with { Version = version, Text = text };
            _csharp = _csharp.SetItem(uri, snapshot); _revision = next;
            return snapshot;
        }
    }
    private SourceText ApplyChanges(SourceText text, IReadOnlyList<LspTextChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.Range is { } range)
            {
                var start = Offset(text, range.Start); var end = Offset(text, range.End);
                if (end < start) throw new LspRequestException(-32602, "The edit range is reversed.");
                if ((long)text.Length - (end - start) + change.Text.Length > maximumCharacters)
                    throw new LspRequestException(-32602, "The document exceeds the source size limit.");
                text = text.WithChanges(new TextChange(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(start, end), change.Text));
            }
            else { ValidateSize(change.Text); text = SourceText.From(change.Text); }
        }
        return text;
    }
    public LspDocumentSnapshot Get(string uri) { lock (_gate) return GetCore(uri); }
    public bool IsCurrent(LspDocumentSnapshot snapshot) { lock (_gate) return _documents.TryGetValue(snapshot.Uri, out var current) && ReferenceEquals(current, snapshot); }
    public void Close(string uri)
    {
        lock (_gate)
        {
            if (!_documents.ContainsKey(uri) && !_csharp.ContainsKey(uri)) return;
            var nextRevision = checked(_revision + 1);
            _documents = _documents.Remove(uri); _csharp = _csharp.Remove(uri); _revision = nextRevision;
        }
    }
    public int GetOffset(LspDocumentSnapshot document, LspPosition position) => Offset(SourceText.From(document.Syntax.Text), position);
    private LspDocumentSnapshot GetCore(string uri) => _documents.TryGetValue(uri, out var value) ? value : throw new LspRequestException(-32602, "The document is not open.");
    private void ValidateSize(string text)
    {
        if (text.Length > maximumCharacters) throw new LspRequestException(-32602, "The document exceeds the source size limit.");
    }
    private static int Offset(SourceText text, LspPosition position)
    {
        if (position.Line < 0 || position.Line >= text.Lines.Count || position.Character < 0)
            throw new LspRequestException(-32602, "The position is outside the document.");
        var line = text.Lines[position.Line];
        if (position.Character > line.Span.Length) throw new LspRequestException(-32602, "The character position exceeds the line's UTF-16 length.");
        return line.Start + position.Character;
    }
    private static string DocumentPath(string text)
    {
        if (!System.Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("file" or "untitled"))
            throw new LspRequestException(-32602, "Only file and untitled document URIs are supported.");
        return uri.IsFile ? uri.LocalPath : text;
    }
}
