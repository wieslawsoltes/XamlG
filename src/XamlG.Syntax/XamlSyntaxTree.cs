using System.Collections.Immutable;
using System.Text;
using System.Threading;
namespace XamlG.Syntax;

/// <summary>An immutable, source-preserving snapshot. Semantic work never changes the source tree.</summary>
public sealed class XamlSyntaxTree
{
    private XamlElementIndex? _elementIndex;
    internal XamlSyntaxTree(string text, string path, long version, XamlParseOptions options, ImmutableArray<XamlSyntaxNode> nodes, ImmutableArray<XamlDiagnostic> diagnostics, XamlParseStatistics? statistics = null)
    { Text = text; Path = path; Version = version; Options = options; Nodes = nodes; Diagnostics = diagnostics; Root = nodes.OfType<XamlElementSyntax>().FirstOrDefault(); Lines = new(text); Statistics = statistics ?? new(text.Length, 0, false); }
    public XamlParseStatistics Statistics { get; }
    public string Text { get; }
    public string Path { get; }
    public long Version { get; }
    public XamlParseOptions Options { get; }
    public ImmutableArray<XamlSyntaxNode> Nodes { get; }
    public ImmutableArray<XamlDiagnostic> Diagnostics { get; }
    public XamlElementSyntax? Root { get; }
    public SourceLineMap Lines { get; }
    public bool HasErrors => Diagnostics.Any(d => d.Severity == XamlSeverity.Error);
    public static XamlSyntaxTree Parse(string text, string path = "", CancellationToken cancellationToken = default, XamlParseOptions? options = null, long version = 0)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        options ??= new();
        if (options.MaximumDepth < 1 || options.MaximumCharacters < 1 || options.MaximumDiagnostics < 1) throw new ArgumentOutOfRangeException(nameof(options));
        var result = new XamlParser(text, options, cancellationToken).Parse();
        return new(text, path, version, options, result.Nodes, result.Diagnostics);
    }
    public XamlSyntaxTree WithChanges(IEnumerable<XamlTextChange> changes, long expectedVersion, CancellationToken cancellationToken = default)
    {
        if (expectedVersion != Version) throw new InvalidOperationException("The edit was computed for a stale source snapshot.");
        var ordered = changes.OrderBy(c => c.Span.Start).ToArray();
        var previousEnd = 0; var previousStart = -1;
        foreach (var change in ordered)
        {
            if (change.NewText == null || change.Span.End > Text.Length || change.Span.Start < previousEnd || change.Span.Start == previousStart) throw new ArgumentException("Edits must be in range and non-overlapping.", nameof(changes));
            previousEnd = change.Span.End; previousStart = change.Span.Start;
        }
        if (ordered.Length == 0) return this;
        var output = new StringBuilder(Text.Length); var offset = 0;
        foreach (var change in ordered) { cancellationToken.ThrowIfCancellationRequested(); output.Append(Text, offset, change.Span.Start - offset); output.Append(change.NewText); offset = change.Span.End; }
        output.Append(Text, offset, Text.Length - offset); var text = output.ToString();
        return text == Text ? this : XamlIncrementalParser.Parse(this, text, ordered, cancellationToken);
    }
    public XamlElementSyntax? FindElement(int position)
    {
        if (Root == null) return null;
        // Source metadata and tooling query the same immutable tree repeatedly.
        // Build once in O(n log n), then perform allocation-free O(log n) lookups
        // instead of a full descendant walk per object. Each edited tree owns its
        // own index; publishing it cannot keep an older source snapshot alive.
        var index = Volatile.Read(ref _elementIndex);
        if (index == null)
        {
            var created = new XamlElementIndex(Root);
            index = Interlocked.CompareExchange(ref _elementIndex, created, null) ?? created;
        }
        return index.Find(position);
    }
    public override string ToString() => Text;
}
