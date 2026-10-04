using System.Collections.Immutable;
using System.Threading;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

/// <summary>Plans simultaneous project resource moves by resolved URI identity, including outgoing
/// relative links in moved documents. Candidate compilation precedes publication; no file I/O or user code runs.</summary>
public sealed class XamlFileRenameService
{
    private readonly XamlCompilationSession _compiler;
    private readonly StringComparer _paths;
    public XamlFileRenameService(XamlCompilationSession compiler, StringComparer? pathComparer = null)
    { _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler)); _paths = pathComparer ?? StringComparer.Ordinal; }

    public XamlFileRenamePlan Plan(IEnumerable<XamlDocumentMove> moves, IEnumerable<XamlAnalysis> analyses,
        CancellationToken cancellationToken = default)
    {
        if (moves == null) throw new ArgumentNullException(nameof(moves));
        if (analyses == null) throw new ArgumentNullException(nameof(analyses));
        cancellationToken.ThrowIfCancellationRequested();
        var requested = moves.Take(257).ToArray();
        if (requested.Length > 256) throw new ArgumentException("At most 256 simultaneous document moves are accepted.", nameof(moves));
        var workspace = analyses.ToDictionary(a => Normalize(a.Syntax.Path), _paths);
        var inputs = _compiler.ProjectDocuments.Select(d => workspace.TryGetValue(Normalize(d.Syntax.Path), out var a)
            ? d with { Syntax = a.Syntax } : throw new InvalidOperationException("A complete project analysis is required: " + d.Syntax.Path)).ToImmutableArray();
        var byPath = inputs.ToDictionary(d => Normalize(d.Syntax.Path), _paths);
        var moving = new Dictionary<string, XamlDocumentMove>(_paths);
        foreach (var move in requested)
        {
            ValidatePhysicalPath(move.OldPath); ValidatePhysicalPath(move.NewPath);
            var oldPath = Normalize(move.OldPath);
            if (!byPath.ContainsKey(oldPath)) throw new ArgumentException("The moved document is not in the loaded project: " + move.OldPath, nameof(moves));
            if (moving.ContainsKey(oldPath)) throw new ArgumentException("A source document occurs in more than one move.", nameof(moves));
            var logical = XamlProjectDocumentStore.NormalizePath(move.NewLogicalPath);
            if (!move.NewPath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && !move.NewPath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The destination must remain a XAML document.", nameof(moves));
            moving.Add(oldPath, move with { NewLogicalPath = logical });
        }
        var originals = inputs.Select(d => workspace[Normalize(d.Syntax.Path)]).ToArray();
        if (originals.Any(a => !a.Output.Success)) throw new InvalidOperationException("Resolve project compiler errors before planning file moves; unresolved references cannot be guessed.");
        var nextPaths = new HashSet<string>(_paths);
        var nextLogicalPaths = new HashSet<string>(StringComparer.Ordinal);
        var nextUris = new HashSet<string>(StringComparer.Ordinal);
        var uriMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var destinations = new Dictionary<string, XamlProjectDocument>(_paths);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Normalize(input.Syntax.Path);
            var oldUri = workspace[path].Document.Options.ResourceUri ?? throw new InvalidOperationException("The project has no resource identity: " + input.Syntax.Path);
            var next = input;
            if (moving.TryGetValue(path, out var move))
            {
                var syntax = XamlSyntaxTree.Parse(input.Syntax.Text, move.NewPath, cancellationToken,
                    options: input.Syntax.Options, version: checked(input.Syntax.Version + 1));
                next = new(syntax, move.NewLogicalPath, move.NewResourceUri ?? input.ResourceUri);
            }
            var nextUri = XamlResourceCatalogBuilder.Address(next, _compiler.Types, _compiler.Profile);
            if (!nextPaths.Add(Normalize(next.Syntax.Path)) || !nextLogicalPaths.Add(next.LogicalPath) || !nextUris.Add(nextUri))
                throw new InvalidOperationException("The move collides with another physical path, logical path or resource address.");
            uriMap.Add(oldUri, nextUri); destinations.Add(path, next);
        }
        var changes = ImmutableArray.CreateBuilder<XamlDocumentEdits>();
        var updated = ImmutableArray.CreateBuilder<XamlProjectDocument>();
        foreach (var original in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Normalize(original.Syntax.Path); var analysis = workspace[path]; var destination = destinations[path];
            var oldBase = analysis.Document.Options.ResourceUri!; var newBase = uriMap[oldBase];
            var references = BoundDocumentTraversal.Expressions(analysis.Document).OfType<BoundResourceExpression>().ToArray();
            var edits = ImmutableArray.CreateBuilder<XamlTextChange>();
            foreach (var site in XamlResourceSourceReader.Read(analysis, _compiler, cancellationToken))
            {
                var oldTarget = XamlResourceUri.Resolve(oldBase, site.Text);
                if (!references.Any(r => r.Resource.Uri == oldTarget && (r.Span.Contains(site.Span) || site.Span.Contains(r.Span))))
                    throw new InvalidOperationException("The source-bearing member is not a statically linked resource: " + original.Syntax.Path);
                var newTarget = uriMap.TryGetValue(oldTarget, out var mapped) ? mapped : oldTarget;
                var replacement = XamlResourceUriRewriter.Rewrite(site.Text, newBase, newTarget);
                if (replacement == null) continue;
                if (!site.CanRewrite) throw new InvalidOperationException("A moved resource reference spans CDATA or multiple text fragments. Normalize its Source value before moving it: " + original.Syntax.Path);
                edits.Add(new(site.Span, XamlResourceUriRewriter.Escape(replacement, site.Quote)));
            }
            var ordered = edits.OrderBy(e => e.Span.Start).ToImmutableArray();
            var text = original.Syntax.WithChanges(ordered, original.Syntax.Version);
            if (!ordered.IsEmpty) changes.Add(new(original.Syntax.Path, original.Syntax.Text, original.Syntax.Version, ordered));
            var final = ReferenceEquals(destination, original) ? text : XamlSyntaxTree.Parse(text.Text, destination.Syntax.Path,
                cancellationToken, options: text.Options, version: checked(original.Syntax.Version + 1));
            updated.Add(destination with { Syntax = final });
        }
        var result = new XamlProjectCompiler().Compile(updated, _compiler.Types.Compilation, _compiler.Profile, _compiler.Options, cancellationToken);
        if (!result.Success) throw new InvalidOperationException("The renamed project is invalid: " + string.Join("; ", result.Documents.SelectMany(d => d.Output.Diagnostics).Select(d => d.Message)));
        return new(moving.Values.ToImmutableArray(), changes.ToImmutable(), inputs, updated.ToImmutable()) { PathComparer = _paths };
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static void ValidatePhysicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl) ||
            Normalize(path).Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("Move paths must be normalized, nonempty source paths.", nameof(path));
    }
}
