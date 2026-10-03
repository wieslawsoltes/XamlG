using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using XamlG.Syntax;

namespace XamlG.LanguageServer;

/// <summary>Atomic UTF-16 document revisions. Changes in a batch are interpreted sequentially, as required by LSP.</summary>
public sealed class LspDocumentStore(int maximumDocuments = 256, int maximumCharacters = 4_194_304)
{
    private readonly object _gate = new();
    private ImmutableDictionary<string, LspDocumentSnapshot> _documents = ImmutableDictionary<string, LspDocumentSnapshot>.Empty.WithComparers(StringComparer.Ordinal);
    public ImmutableArray<LspDocumentSnapshot> Snapshots { get { lock (_gate) return _documents.Values.ToImmutableArray(); } }

    public LspDocumentSnapshot Open(string uri, int version, string text)
    {
        ValidateSize(text);
        var path = DocumentPath(uri);
        lock (_gate)
        {
            if (_documents.ContainsKey(uri)) throw new LspRequestException(-32602, "The document is already open.");
            if (_documents.Count >= maximumDocuments) throw new LspRequestException(-32602, "The open-document limit has been reached.");
            var snapshot = new LspDocumentSnapshot(uri, version, XamlSyntaxTree.Parse(text, path, version: version));
            _documents = _documents.Add(uri, snapshot);
            return snapshot;
        }
    }

    public LspDocumentSnapshot Change(string uri, int version, IReadOnlyList<LspTextChange> changes)
    {
        lock (_gate)
        {
            var previous = GetCore(uri);
            if (version <= previous.Version) throw new LspRequestException(-32801, "The document change has a stale version.");
            var text = SourceText.From(previous.Syntax.Text);
            foreach (var change in changes)
            {
                if (change.Range is { } range)
                {
                    var start = Offset(text, range.Start);
                    var end = Offset(text, range.End);
                    if (end < start) throw new LspRequestException(-32602, "The edit range is reversed.");
                    if ((long)text.Length - (end - start) + change.Text.Length > maximumCharacters)
                        throw new LspRequestException(-32602, "The document exceeds the source size limit.");
                    text = text.WithChanges(new TextChange(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(start, end), change.Text));
                }
                else { ValidateSize(change.Text); text = SourceText.From(change.Text); }
            }
            var snapshot = new LspDocumentSnapshot(uri, version, XamlSyntaxTree.Parse(text.ToString(), previous.Syntax.Path, options: previous.Syntax.Options, version: version));
            _documents = _documents.SetItem(uri, snapshot);
            return snapshot;
        }
    }

    public LspDocumentSnapshot Get(string uri) { lock (_gate) return GetCore(uri); }
    public bool IsCurrent(LspDocumentSnapshot snapshot) { lock (_gate) return _documents.TryGetValue(snapshot.Uri, out var current) && ReferenceEquals(current, snapshot); }
    public void Close(string uri) { lock (_gate) _documents = _documents.Remove(uri); }
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
