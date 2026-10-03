using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>One immutable project compilation and a bounded cache of document analyses. Safe for concurrent readers.</summary>
public sealed class XamlCompilationSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, XamlAnalysis> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly int _capacity;

    public XamlCompilationSession(CSharpCompilation compilation, XamlFrameworkProfile? profile = null,
        XamlCompilerOptions? options = null, int cacheCapacity = 128)
    {
        if (cacheCapacity < 1) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        Profile = profile ?? XamlFrameworkProfile.Portable;
        Options = options ?? new();
        Types = new(compilation, Profile.TypeSystem);
        _capacity = cacheCapacity;
    }

    public RoslynTypeSystem Types { get; }
    public XamlFrameworkProfile Profile { get; }
    public XamlCompilerOptions Options { get; }

    public XamlAnalysis Analyze(XamlSyntaxTree syntax, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            if (_cache.TryGetValue(syntax.Path, out var cached) && ReferenceEquals(cached.Syntax, syntax)) return cached;

        var bound = new XamlCompiler().Bind(syntax, Types, Profile, Options, cancellationToken);
        var output = new CSharpEmitter().Emit(bound, cancellationToken);
        var analysis = new XamlAnalysis(syntax, bound, output);
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

    public void ClearCache()
    {
        lock (_gate) { _cache.Clear(); _insertionOrder.Clear(); }
    }
}
