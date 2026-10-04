using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>One immutable Roslyn project, optional resource document set and bounded overlay analysis cache.</summary>
public sealed class XamlCompilationSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, XamlAnalysis> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly ImmutableArray<XamlProjectDocument> _documents;
    private readonly ImmutableDictionary<string, XamlProjectDocument> _documentPaths;
    private readonly int _capacity;
    private XamlProjectCompilation? _baseline;

    public XamlCompilationSession(CSharpCompilation compilation, XamlFrameworkProfile? profile = null,
        XamlCompilerOptions? options = null, int cacheCapacity = 128, IEnumerable<XamlProjectDocument>? projectDocuments = null)
    {
        if (cacheCapacity < 1) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        Profile = profile ?? XamlFrameworkProfile.Portable;
        Options = options ?? new();
        Types = new(compilation, Profile.TypeSystem);
        _capacity = cacheCapacity;
        _documents = projectDocuments?.ToImmutableArray() ?? ImmutableArray<XamlProjectDocument>.Empty;
        _documentPaths = _documents.ToImmutableDictionary(d => Normalize(d.Syntax.Path), StringComparer.Ordinal);
    }
    public RoslynTypeSystem Types { get; }
    public XamlFrameworkProfile Profile { get; }
    public XamlCompilerOptions Options { get; }

    public XamlAnalysis Analyze(XamlSyntaxTree syntax, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            if (_cache.TryGetValue(syntax.Path, out var cached) && ReferenceEquals(cached.Syntax, syntax)) return cached;
        XamlAnalysis analysis;
        if (_documentPaths.TryGetValue(Normalize(syntax.Path), out var document))
        {
            var project = ReferenceEquals(document.Syntax, syntax) ? Baseline(cancellationToken) :
                new XamlProjectCompiler().Compile(_documents.Select(d => ReferenceEquals(d, document) ? d with { Syntax = syntax } : d),
                    Types.Compilation, Profile, Options, cancellationToken);
            var result = project.Documents.Single(d => ReferenceEquals(d.Input.Syntax, syntax));
            analysis = new(syntax, result.Document, result.Output);
        }
        else
        {
            var bound = new XamlCompiler().Bind(syntax, Types, Profile, Options, cancellationToken);
            analysis = new(syntax, bound, new CSharpEmitter().Emit(bound, cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_cache.ContainsKey(syntax.Path))
            {
                while (_cache.Count >= _capacity) _cache.Remove(_insertionOrder.Dequeue());
                _insertionOrder.Enqueue(syntax.Path);
            }
            _cache[syntax.Path] = analysis;
        }
        return analysis;
    }

    /// <summary>Compiles a complete document set. Cross-file diagnostics and exported factories match the generator host.</summary>
    public ImmutableArray<XamlAnalysis> AnalyzeProject(IEnumerable<XamlSyntaxTree> documents, CancellationToken cancellationToken = default)
    {
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        var inputs = documents.Select(syntax => _documentPaths.TryGetValue(Normalize(syntax.Path), out var known)
            ? known with { Syntax = syntax } : new XamlProjectDocument(syntax, syntax.Path)).ToArray();
        var project = new XamlProjectCompiler().Compile(inputs, Types.Compilation, Profile, Options, cancellationToken);
        return project.Documents.Select(d => new XamlAnalysis(d.Input.Syntax, d.Document, d.Output)).ToImmutableArray();
    }

    private XamlProjectCompilation Baseline(CancellationToken cancellationToken)
    {
        lock (_gate) if (_baseline != null) return _baseline;
        var project = new XamlProjectCompiler().Compile(_documents, Types.Compilation, Profile, Options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return _baseline ??= project;
    }
    public void ClearCache()
    {
        lock (_gate) { _cache.Clear(); _insertionOrder.Clear(); _baseline = null; }
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
}
