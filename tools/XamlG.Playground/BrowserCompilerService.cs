using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Frameworks;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.Playground;

/// <summary>The browser is a host of the production compiler, not a JavaScript imitation of its semantics.</summary>
public sealed class BrowserCompilerService(HttpClient http)
{
    private ImmutableArray<MetadataReference> _references = ImmutableArray<MetadataReference>.Empty;
    private int _assemblySequence;
    private int _loadedAssemblies;
    public bool IsReady => !_references.IsEmpty;
    public int ReferenceCount => _references.Length;

    public async Task InitializeAsync(Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsReady) return;
        var manifest = await http.GetStringAsync("references/index.txt", cancellationToken);
        var names = manifest.TrimStart('\uFEFF').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length is 0 or > 1024 || names.Any(n => !n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(n) != n || n.Contains('\\')))
            throw new InvalidDataException("The compiler metadata manifest is invalid.");
        var references = new MetadataReference[names.Length];
        using var throttle = new SemaphoreSlim(6);
        var completed = 0;
        await Task.WhenAll(names.Select(async (name, index) =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var bytes = await http.GetByteArrayAsync("references/" + Uri.EscapeDataString(name), cancellationToken);
                if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("A metadata image exceeds the configured size limit.");
                references[index] = MetadataReference.CreateFromImage(ImmutableArray.Create(bytes), filePath: name);
                progress?.Invoke(Interlocked.Increment(ref completed), names.Length);
            }
            finally { throttle.Release(); }
        }));
        _references = references.ToImmutableArray();
    }

    public BrowserCompilation Analyze(string xaml, string code, string framework = "Avalonia", CancellationToken cancellationToken = default)
    {
        if (!IsReady) throw new InvalidOperationException("Compiler metadata has not finished loading.");
        var clock = Stopwatch.StartNew();
        var name = "XamlG.Playground.Generated_" + Interlocked.Increment(ref _assemblySequence);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText(code, parseOptions, "Code.cs", cancellationToken: cancellationToken) },
            _references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
        var profile = KnownFrameworkProfiles.Select(compilation, framework);
        var session = new XamlCompilationSession(compilation, profile,
            new XamlCompilerOptions { DocumentId = "View.axaml", BaseUri = "avares://" + name + "/View.axaml" });
        var analysis = session.Analyze(XamlSyntaxTree.Parse(xaml, "View.axaml", cancellationToken), cancellationToken);
        var diagnostics = ImmutableArray.CreateBuilder<PlaygroundDiagnostic>();
        foreach (var item in analysis.Output.Diagnostics)
        {
            var start = analysis.Syntax.Lines.GetPosition(Math.Min(item.Span.Start, xaml.Length));
            var end = analysis.Syntax.Lines.GetPosition(Math.Min(item.Span.End, xaml.Length));
            diagnostics.Add(new(item.Code, item.Message, item.Severity.ToString(), "View.axaml", start.Line + 1, start.Character + 1, end.Line + 1, end.Character + 1));
        }
        if (analysis.Output.Success)
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(analysis.Output.Source, parseOptions, analysis.Output.HintName, cancellationToken: cancellationToken));
        foreach (var item in compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
        {
            var location = item.Location.GetMappedLineSpan();
            diagnostics.Add(new(item.Id, item.GetMessage(), item.Severity.ToString(), location.Path,
                location.StartLinePosition.Line + 1, location.StartLinePosition.Character + 1,
                location.EndLinePosition.Line + 1, location.EndLinePosition.Character + 1));
        }
        clock.Stop();
        return new(analysis, compilation, diagnostics.ToImmutable(), clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>Explicit execution only. Browser assemblies are bounded because collectible load contexts are not assumed.</summary>
    public object Run(BrowserCompilation result)
    {
        if (!result.Success) throw new InvalidOperationException("Resolve the compiler errors before running this document.");
        if (_loadedAssemblies >= 64) throw new InvalidOperationException("This tab has loaded 64 preview assemblies. Export the document and reload the page to reclaim the runtime.");
        using var image = new MemoryStream();
        var emitted = result.Compilation.Emit(image);
        if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var assembly = Assembly.Load(image.ToArray());
        _loadedAssemblies++;
        var output = result.Analysis.Output;
        var factory = assembly.GetType(output.FactoryTypeName, throwOnError: true)!;
        if (output.BuildMethodName != null)
            return factory.GetMethod(output.BuildMethodName)!.Invoke(null, new object?[] { null })!;
        var instance = Activator.CreateInstance(factory) ?? throw new InvalidOperationException("The code-behind root could not be constructed.");
        if (!XamlG.Runtime.XamlRuntimeSession.TryGet(instance, out _))
            factory.GetMethod(output.PopulateMethodName)!.Invoke(null, new object?[] { instance, null });
        return instance;
    }
}
