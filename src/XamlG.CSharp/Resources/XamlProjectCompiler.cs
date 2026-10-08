using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Integration;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

/// <summary>Host-independent project binding, resource linking and source integration.
/// Retains one compilation and one binding/output per logical document; calls are serialized.</summary>
public sealed class XamlProjectCompiler
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedProjectDocument> _documents = new(StringComparer.Ordinal);
    private CSharpCompilation? _compilation;
    private XamlFrameworkProfile? _profile;
    private XamlCompilerOptions? _options;
    private RoslynTypeSystem? _types;
    private XamlResourceCatalog? _catalog;
    private readonly XamlLoaderAdapterCompiler _loader = new();

    public XamlProjectCompilation Compile(IEnumerable<XamlProjectDocument> documents, CSharpCompilation compilation,
        XamlFrameworkProfile? profile = null, XamlCompilerOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = documents.OrderBy(d => d.LogicalPath, StringComparer.Ordinal).ToArray();
        if (inputs.Length > 16384) throw new ArgumentException("A project cannot exceed 16384 XAML documents.", nameof(documents));
        profile ??= XamlFrameworkProfile.Portable; options ??= new();
        if (options.MaxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException(nameof(options), "Maximum document concurrency must be positive.");
        options = options with
        {
            IsPrecompilation = true,
            GeneratedNamespace = options.GeneratedNamespace + ".Assembly_" + CSharpNames.StableId(compilation.Assembly.Identity.Name),
            SourceLoader = options.AdaptLoaderCalls ? options.SourceLoader ?? profile.SourceLoader : null
        };
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(compilation, _compilation) || !Equals(profile, _profile) || !Equals(options, _options))
            {
                ClearCore(); _compilation = compilation; _profile = profile; _options = options;
                _types = new(compilation, profile.TypeSystem);
            }
            var candidate = XamlResourceCatalogBuilder.Create(inputs, _types!, profile, options, cancellationToken);
            if (_catalog == null || !ResourceCatalogEquivalence.Equals(_catalog, candidate))
            { _documents.Clear(); _catalog = candidate; }
            var project = CompileCore(inputs, _types!, profile, options, _catalog, cancellationToken);
            return project with { SourceIntegration = _loader.Compile(compilation, project, options.SourceLoader, cancellationToken) };
        }
    }
    public void ClearCache() { lock (_gate) ClearCore(); }
    private void ClearCore()
    {
        _documents.Clear(); _catalog = null; _types = null;
        _compilation = null; _profile = null; _options = null;
    }
    private XamlProjectCompilation CompileCore(XamlProjectDocument[] inputs, RoslynTypeSystem types,
        XamlFrameworkProfile profile, XamlCompilerOptions options, XamlResourceCatalog catalog, CancellationToken cancellationToken)
    {
        var livePaths = new HashSet<string>(inputs.Select(d => d.LogicalPath), StringComparer.Ordinal);
        foreach (var key in _documents.Keys.Where(key => !livePaths.Contains(key)).ToArray()) _documents.Remove(key);
        var bound = new BoundDocument[inputs.Length];
        var entries = new CachedProjectDocument[inputs.Length];
        var addresses = new string?[inputs.Length];
        var duplicates = new HashSet<string>(inputs.Where(d => profile.Directives.ShouldCompile(d.Syntax, options))
            .GroupBy(d => d.LogicalPath, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.Ordinal);
        var boundCount = 0; var reusedBindings = 0;
        ForEachDocument(inputs.Length, options, cancellationToken, i =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = inputs[i]; string? addressError = null;
            try { addresses[i] = XamlResourceCatalogBuilder.Address(input, types, profile); }
            catch (ArgumentException error) { addressError = error.Message; }
            if (!duplicates.Contains(input.LogicalPath) && _documents.TryGetValue(input.LogicalPath, out var cached) &&
                ReferenceEquals(cached.Input.Syntax, input.Syntax) && cached.Input.ResourceUri == input.ResourceUri)
            { entries[i] = cached; bound[i] = cached.Document; Interlocked.Increment(ref reusedBindings); }
            else
            {
                var itemOptions = options with
                { DocumentId = input.LogicalPath, ResourceUri = addresses[i], BaseUri = addresses[i] ?? options.BaseUri, Resources = catalog };
                var document = new XamlCompiler().Bind(input.Syntax, types, profile, itemOptions, cancellationToken);
                entries[i] = new(input, document); bound[i] = document; Interlocked.Increment(ref boundCount);
            }
            if (!bound[i].IsSkipped)
            {
                if (addressError != null) bound[i] = AddError(bound[i], "XG3300", addressError);
                if (duplicates.Contains(input.LogicalPath)) bound[i] = AddError(bound[i], "XG3300", "Duplicate logical XAML path: " + input.LogicalPath);
            }
        });
        // Keep the cache read-only while workers bind and publish in stable input order.
        for (var i = 0; i < inputs.Length; i++)
            if (!duplicates.Contains(inputs[i].LogicalPath)) _documents[inputs[i].LogicalPath] = entries[i];
        foreach (var group in addresses.Select((uri, index) => (Uri: uri, Index: index)).Where(p => p.Uri != null && !bound[p.Index].IsSkipped).GroupBy(p => p.Uri, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var item in group) bound[item.Index] = AddError(bound[item.Index], "XG3300", "Duplicate resource URI: " + item.Uri);
        foreach (var group in bound.Select((d, i) => (Document: d, Index: i)).Where(p => !p.Document.IsSkipped && p.Document.ClassName != null).GroupBy(p => p.Document.ClassName, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var item in group) bound[item.Index] = AddError(item.Document, "XG2002", "More than one XAML document declares x:Class '" + group.Key + "'.");
        XamlResourceGraph.Validate(bound, cancellationToken);
        // Regenerate only outputs whose shared accessor layout changed; their bound
        // documents remain reusable. Keep successful builds independent of edit history.
        var properties = entries.Where((entry, index) => !ReferenceEquals(bound[index], entry.Document) || entry.Output == null).Any()
            ? SharedPropertyTables.Create(bound, types, options.GeneratedNamespace, cancellationToken)
            : null;
        // During binding failures cached survivors keep their complete, valid helpers.
        var checkPropertyLayouts = properties != null && bound.All(document => document.Success);
        var emissions = new XamlEmissionResult[inputs.Length]; var emittedCount = 0; var reusedOutputs = 0;
        ForEachDocument(inputs.Length, options, cancellationToken, i =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            XamlEmissionResult emission;
            if (ReferenceEquals(bound[i], entries[i].Document) && entries[i].Output is { } cachedOutput &&
                (!checkPropertyLayouts || cachedOutput.PropertyLayout == (properties![i]?.Identity ?? string.Empty)))
            { emission = cachedOutput; Interlocked.Increment(ref reusedOutputs); }
            else
            {
                emission = new CSharpEmitter().Emit(bound[i], cancellationToken, shareServices: true, properties?[i], exportResources: true); Interlocked.Increment(ref emittedCount);
                if (ReferenceEquals(bound[i], entries[i].Document))
                {
                    entries[i].Output = emission;
                    entries[i].OutputWithHelpers = null;
                    entries[i].PublishedHelpers = null;
                }
            }
            emissions[i] = emission;
        });
        XamlResourceGraph.ValidateEmissions(bound, emissions, cancellationToken);
        // Publish each identical helper implementation once, in stable input order.
        // Keep the cached base output independent of ownership so additions/removals
        // can move a shared definition without rebinding unaffected documents.
        var published = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < emissions.Length; i++)
        {
            var emission = emissions[i];
            if (!emission.Success) continue;
            var owned = emission.SharedSources.Where(source => published.Add(source.TypeName)).ToArray();
            if (owned.Length == 0) continue;
            var identity = string.Join("\n", owned.Select(source => source.TypeName));
            if (ReferenceEquals(emission, entries[i].Output))
            {
                var entry = entries[i];
                if (entry.PublishedHelpers != identity)
                {
                    entry.PublishedHelpers = identity;
                    entry.OutputWithHelpers = emission with { Source = emission.Source + "\n" + string.Join("\n", owned.Select(source => source.Source)) };
                }
                emissions[i] = entry.OutputWithHelpers!;
            }
            else emissions[i] = emission with { Source = emission.Source + "\n" + string.Join("\n", owned.Select(source => source.Source)) };
        }
        var output = inputs.Select((input, index) => new XamlProjectDocumentResult(input, addresses[index], bound[index], emissions[index])).ToImmutableArray();
        return new(output, catalog) { Statistics = new(boundCount, reusedBindings, emittedCount, reusedOutputs) };
    }
    private static void ForEachDocument(int count, XamlCompilerOptions options, CancellationToken cancellationToken, Action<int> action)
    {
        var concurrency = Math.Min(options.MaxDegreeOfParallelism, Environment.ProcessorCount);
        if (concurrency == 1 || count < 2)
        { for (var i = 0; i < count; i++) { cancellationToken.ThrowIfCancellationRequested(); action(i); } }
        else
            Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = concurrency }, action);
    }
    private static BoundDocument AddError(BoundDocument document, string code, string message) => document with
    { Diagnostics = document.Diagnostics.Add(new XamlDiagnostic(code, message, document.Syntax.Root?.NameSpan ?? new TextSpan(0, 0))) };
}
