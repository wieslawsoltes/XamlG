using System.Threading;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>Revision-checked source editing with bounded undo/redo. Every published revision is monotonic, including undo.</summary>
public sealed class XamlDocumentSession
{
    private readonly object _gate = new();
    private readonly LinkedList<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly int _historyCapacity;
    private readonly long _historyCharacterLimit;
    private long _undoCharacters;
    private XamlSyntaxTree _current;

    public XamlDocumentSession(string text, string path = "Document.xaml", int historyCapacity = 100,
        long historyCharacterLimit = 8_388_608)
    {
        if (historyCapacity < 1 || historyCharacterLimit < 1) throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        _current = XamlSyntaxTree.Parse(text, path);
        _historyCapacity = historyCapacity;
        _historyCharacterLimit = historyCharacterLimit;
    }

    public XamlSyntaxTree Current { get { lock (_gate) return _current; } }
    public bool CanUndo { get { lock (_gate) return _undo.Count != 0; } }
    public bool CanRedo { get { lock (_gate) return _redo.Count != 0; } }

    public XamlSyntaxTree Apply(XamlEditTransaction transaction, bool requireWellFormed = false,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var candidate = _current.WithChanges(transaction.Changes, transaction.ExpectedRevision, cancellationToken);
            if (requireWellFormed && candidate.HasErrors)
                throw new InvalidOperationException("The designer edit would produce malformed XAML: " + string.Join("; ", candidate.Diagnostics.Select(d => d.Message)));
            if (ReferenceEquals(candidate, _current)) return _current;
            Remember(_current.Text);
            _redo.Clear();
            return _current = candidate;
        }
    }

    public XamlSyntaxTree Undo(long expectedRevision) => Navigate(expectedRevision, true);
    public XamlSyntaxTree Redo(long expectedRevision) => Navigate(expectedRevision, false);

    private XamlSyntaxTree Navigate(long expectedRevision, bool undo)
    {
        lock (_gate)
        {
            if (_current.Version != expectedRevision) throw new InvalidOperationException("History navigation requires the current revision.");
            if (undo ? _undo.Count == 0 : _redo.Count == 0) return _current;
            string text;
            if (undo)
            {
                text = _undo.Last!.Value;
                _undo.RemoveLast();
                _undoCharacters -= text.Length;
                _redo.Push(_current.Text);
            }
            else { text = _redo.Pop(); Remember(_current.Text); }
            return _current = XamlSyntaxTree.Parse(text, _current.Path, options: _current.Options,
                version: checked(_current.Version + 1));
        }
    }

    private void Remember(string text)
    {
        _undo.AddLast(text);
        _undoCharacters += text.Length;
        while (_undo.Count > _historyCapacity || _undoCharacters > _historyCharacterLimit)
        {
            _undoCharacters -= _undo.First!.Value.Length;
            _undo.RemoveFirst();
        }
    }
}
