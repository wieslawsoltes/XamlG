using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>A bounded in-memory project document set. Updates retain syntax identity and reject stale
/// document revisions. Replacement is atomic, including validation and document/character budgets.</summary>
public sealed class XamlProjectDocumentStore
{
    private readonly object _gate = new();
    private readonly ImmutableHashSet<string> _reserved;
    private readonly int _maximumDocuments;
    private readonly int _maximumCharacters;
    private ImmutableDictionary<string, XamlSyntaxTree> _documents = ImmutableDictionary<string, XamlSyntaxTree>.Empty.WithComparers(StringComparer.Ordinal);
    private int _characters;
    private long _revision;

    public XamlProjectDocumentStore(IEnumerable<string>? reservedPaths = null, int maximumDocuments = 128, int maximumCharacters = 4_194_304)
    {
        if (maximumDocuments < 1 || maximumCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumDocuments));
        _maximumDocuments = maximumDocuments; _maximumCharacters = maximumCharacters;
        _reserved = (reservedPaths ?? Array.Empty<string>()).Select(NormalizePath).ToImmutableHashSet(StringComparer.Ordinal);
    }
    public long Revision { get { lock (_gate) return _revision; } }
    public ImmutableDictionary<string, XamlSyntaxTree> Snapshot { get { lock (_gate) return _documents; } }

    public XamlSyntaxTree Add(string path, string text)
    {
        path = ValidatePath(path); ValidateText(text);
        lock (_gate)
        {
            if (_documents.ContainsKey(path)) throw new ArgumentException("A document already exists at '" + path + "'.", nameof(path));
            if (_documents.Count >= _maximumDocuments || (long)_characters + text.Length > _maximumCharacters)
                throw new InvalidOperationException("The project document or character budget would be exceeded.");
            var revision = checked(_revision + 1);
            var syntax = XamlSyntaxTree.Parse(text, path, version: revision);
            _documents = _documents.Add(path, syntax); _characters += text.Length; _revision = revision;
            return syntax;
        }
    }
    public XamlSyntaxTree Update(string path, long expectedVersion, string text)
    {
        path = ValidatePath(path); ValidateText(text);
        lock (_gate)
        {
            if (!_documents.TryGetValue(path, out var previous)) throw new ArgumentException("The document is not in this project.", nameof(path));
            if (previous.Version != expectedVersion) throw new InvalidOperationException("The edit belongs to an older project document.");
            if ((long)_characters - previous.Text.Length + text.Length > _maximumCharacters)
                throw new InvalidOperationException("The project character budget would be exceeded.");
            var updated = previous.WithChanges(XamlTextDiffer.GetChanges(previous.Text, text), expectedVersion);
            if (ReferenceEquals(previous, updated)) return previous;
            var revision = checked(_revision + 1);
            _documents = _documents.SetItem(path, updated);
            _characters += text.Length - previous.Text.Length; _revision = revision;
            return updated;
        }
    }
    public void Remove(string path, long expectedVersion)
    {
        path = ValidatePath(path);
        lock (_gate)
        {
            if (!_documents.TryGetValue(path, out var current) || current.Version != expectedVersion)
                throw new InvalidOperationException("The remove request belongs to a missing or older document.");
            var revision = checked(_revision + 1);
            _documents = _documents.Remove(path); _characters -= current.Text.Length; _revision = revision;
        }
    }
    public void ReplaceAll(IReadOnlyDictionary<string, string> documents)
    {
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        if (documents.Count > _maximumDocuments) throw new InvalidOperationException("The project document budget would be exceeded.");
        lock (_gate)
        {
            var revision = checked(_revision + 1);
            var replacement = ImmutableDictionary.CreateBuilder<string, XamlSyntaxTree>(StringComparer.Ordinal);
            long characters = 0;
            foreach (var item in documents)
            {
                var path = ValidatePath(item.Key); ValidateText(item.Value);
                characters += item.Value.Length;
                if (characters > _maximumCharacters) throw new InvalidOperationException("The project character budget would be exceeded.");
                replacement.Add(path, XamlSyntaxTree.Parse(item.Value, path, version: revision));
            }
            _documents = replacement.ToImmutable(); _characters = (int)characters; _revision = revision;
        }
    }
    private string ValidatePath(string path)
    {
        var result = NormalizePath(path);
        if (_reserved.Contains(result)) throw new ArgumentException("This path is owned by the host: " + result, nameof(path));
        return result;
    }
    private void ValidateText(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (text.Length > _maximumCharacters) throw new InvalidOperationException("The document exceeds the project character budget.");
    }
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024) throw new ArgumentException("A project-relative XAML path of at most 1024 characters is required.", nameof(path));
        path = path.Replace('\\', '/');
        if (path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0 || path.Any(char.IsControl) ||
            path.Split('/').Any(part => part.Length == 0 || part is "." or "..") ||
            !(path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The document path must be a normalized project-relative .xaml or .axaml path.", nameof(path));
        return path;
    }
}
