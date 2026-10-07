using System.Collections.Immutable;

namespace XamlG.Tooling;

/// <summary>Bounded C# source snapshots for hosts with in-memory projects. An immutable
/// document identity and monotonically increasing version prevent retired editors from
/// overwriting a replacement file. This store never compiles or executes source.</summary>
public sealed class CSharpProjectDocumentStore
{
    private readonly object _gate = new();
    private readonly ImmutableHashSet<string> _reserved;
    private readonly int _maximumDocuments, _maximumCharacters;
    private ImmutableDictionary<string, CSharpProjectDocument> _documents = ImmutableDictionary<string, CSharpProjectDocument>.Empty.WithComparers(StringComparer.Ordinal);
    private long _revision;
    private int _characters;

    public CSharpProjectDocumentStore(IEnumerable<string>? reservedPaths = null, int maximumDocuments = 128, int maximumCharacters = 4_194_304)
    {
        if (maximumDocuments < 1 || maximumCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumDocuments));
        _reserved = (reservedPaths ?? Array.Empty<string>()).Select(NormalizePath).ToImmutableHashSet(StringComparer.Ordinal);
        _maximumDocuments = maximumDocuments; _maximumCharacters = maximumCharacters;
    }
    public long Revision { get { lock (_gate) return _revision; } }
    public ImmutableDictionary<string, CSharpProjectDocument> Snapshot { get { lock (_gate) return _documents; } }

    public CSharpProjectDocument Add(string path, string text)
    {
        path = ValidatePath(path); ValidateText(text);
        lock (_gate)
        {
            if (_documents.ContainsKey(path)) throw new ArgumentException("A source document already exists at " + path, nameof(path));
            if (_documents.Count >= _maximumDocuments || (long)_characters + text.Length > _maximumCharacters)
                throw new InvalidOperationException("The C# document or character budget would be exceeded.");
            var document = new CSharpProjectDocument(path, text, checked(_revision + 1));
            _documents = _documents.Add(path, document); _characters += text.Length; _revision = document.Version;
            return document;
        }
    }
    public CSharpProjectDocument Update(string path, long expectedVersion, string text)
    {
        path = ValidatePath(path); ValidateText(text);
        lock (_gate)
        {
            var previous = Current(path, expectedVersion);
            if (previous.Text == text) return previous;
            if ((long)_characters - previous.Text.Length + text.Length > _maximumCharacters)
                throw new InvalidOperationException("The C# character budget would be exceeded.");
            var updated = new CSharpProjectDocument(path, text, checked(_revision + 1));
            _documents = _documents.SetItem(path, updated); _characters += text.Length - previous.Text.Length; _revision = updated.Version;
            return updated;
        }
    }
    public void Remove(string path, long expectedVersion)
    {
        path = ValidatePath(path);
        lock (_gate)
        {
            var previous = Current(path, expectedVersion); var revision = checked(_revision + 1);
            _documents = _documents.Remove(path); _characters -= previous.Text.Length; _revision = revision;
        }
    }
    public void ReplaceAll(IReadOnlyDictionary<string, string> documents)
    {
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        if (documents.Count > _maximumDocuments) throw new InvalidOperationException("The C# document budget would be exceeded.");
        lock (_gate)
        {
            var revision = checked(_revision + 1); long characters = 0;
            var replacement = ImmutableDictionary.CreateBuilder<string, CSharpProjectDocument>(StringComparer.Ordinal);
            foreach (var item in documents)
            {
                var path = ValidatePath(item.Key); ValidateText(item.Value);
                characters += item.Value.Length;
                if (characters > _maximumCharacters) throw new InvalidOperationException("The C# character budget would be exceeded.");
                replacement.Add(path, new(path, item.Value, revision));
            }
            _documents = replacement.ToImmutable(); _characters = (int)characters; _revision = revision;
        }
    }
    private CSharpProjectDocument Current(string path, long version) =>
        _documents.TryGetValue(path, out var document) && document.Version == version ? document :
            throw new InvalidOperationException("The edit belongs to a missing or replaced C# document.");
    private string ValidatePath(string path)
    {
        path = NormalizePath(path);
        return _reserved.Contains(path) ? throw new ArgumentException("This source path is owned by the host: " + path, nameof(path)) : path;
    }
    private void ValidateText(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (text.Length > _maximumCharacters) throw new InvalidOperationException("The C# document exceeds the character budget.");
    }
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024) throw new ArgumentException("A project-relative C# path of at most 1024 characters is required.", nameof(path));
        path = path.Replace('\\', '/');
        if (path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0 || path.Any(char.IsControl) ||
            path.Split('/').Any(part => part.Length == 0 || part is "." or "..") || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a normalized project-relative .cs path.", nameof(path));
        return path;
    }
}

public sealed record CSharpProjectDocument(string Path, string Text, long Version);
