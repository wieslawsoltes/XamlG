using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Frameworks;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.Playground;

/// <summary>All hosts consume the same project factories and loader adapters. Application code
/// is loaded only by explicit trusted Run or inside the separate isolated execution host.</summary>
public sealed class BrowserCompilerService(HttpClient http)
{
    private ImmutableArray<MetadataReference> _references = ImmutableArray<MetadataReference>.Empty;
    private int _assemblySequence;
    private int _loadedAssemblies;
    private CSharpCompilationSettings _settings = new CSharpCompilationSettings().Normalize();
    public XamlProjectDocumentStore Resources { get; } = new(new[] { "View.axaml" });
    public CSharpProjectDocumentStore CodeFiles { get; } = new(new[] { "Code.cs" });
    public bool IsReady => !_references.IsEmpty;
    public int ReferenceCount => _references.Length;
    public CSharpCompilationSettings Settings => _settings;
    public long SettingsRevision { get; private set; }
    public IReadOnlyList<string> AvailableReferenceNames => _references.Select(reference => reference.Display!).Order(StringComparer.Ordinal).ToArray();
    public CSharpCompilationSettings ValidateSettings(CSharpCompilationSettings settings)
    {
        var normalized = settings.Normalize();
        if (normalized.ReferenceNames != null)
        {
            var available = AvailableReferenceNames.ToHashSet(StringComparer.Ordinal);
            var unknown = normalized.ReferenceNames.Where(name => !available.Contains(name)).Take(5).ToArray();
            if (unknown.Length != 0) throw new ArgumentException("Unknown host metadata references: " + string.Join(", ", unknown));
        }
        return normalized;
    }
    public void SetSettings(CSharpCompilationSettings settings)
    {
        var candidate = ValidateSettings(settings);
        if (JsonSerializer.Serialize(candidate) == JsonSerializer.Serialize(_settings)) return;
        _settings = candidate; SettingsRevision++;
    }

    public async Task InitializeAsync(Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsReady) return;
        var manifest = await ReadMetadataAsync("references/index.txt", (content, token) => content.ReadAsStringAsync(token), cancellationToken);
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
                var bytes = await ReadMetadataAsync("references/" + Uri.EscapeDataString(name), (content, token) => content.ReadAsByteArrayAsync(token), cancellationToken);
                if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("A metadata image exceeds the configured size limit.");
                references[index] = MetadataReference.CreateFromImage(ImmutableArray.Create(bytes), filePath: name);
                progress?.Invoke(Interlocked.Increment(ref completed), names.Length);
            }
            finally { throttle.Release(); }
        }));
        _references = references.ToImmutableArray();
    }

    // Only these idempotent, same-origin metadata downloads retry. Provider
    // requests and IDE operations retain their independent recovery policies.
    private async Task<T> ReadMetadataAsync<T>(string path, Func<HttpContent, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var delay = TimeSpan.FromMilliseconds(500 * (attempt + 1));
            try
            {
                using var response = await http.GetAsync(path, cancellationToken);
                if (response.IsSuccessStatusCode) return await read(response.Content, cancellationToken);
                var retryAfter = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
                if (attempt >= 2 || !IsTransientMetadataStatus(response.StatusCode) || retryAfter > TimeSpan.FromSeconds(15))
                    throw new HttpRequestException($"Could not download compiler metadata '{path}' (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
                if (retryAfter > delay) delay = retryAfter;
            }
            catch (HttpRequestException error) when (error.StatusCode == null && attempt < 2 && !cancellationToken.IsCancellationRequested)
            {
                // A transport failure before a usable response can also recover.
            }
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsTransientMetadataStatus(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    public BrowserCompilation Analyze(string xaml, string code, string framework = "Avalonia", CancellationToken cancellationToken = default) =>
        Analyze(XamlSyntaxTree.Parse(xaml, "View.axaml", cancellationToken), code, framework, cancellationToken);

    public BrowserCompilation Analyze(XamlSyntaxTree syntax, string code, string framework = "Avalonia", CancellationToken cancellationToken = default,
        IReadOnlyCollection<XamlSyntaxTree>? resourceDocuments = null, CSharpCompilationSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        if (!IsReady) throw new InvalidOperationException("Compiler metadata has not finished loading.");
        var clock = Stopwatch.StartNew();
        var name = "XamlG.Playground.Generated_" + Interlocked.Increment(ref _assemblySequence);
        var options = settings == null ? _settings : ValidateSettings(settings);
        var settingsRevision = SettingsRevision;
        var parseOptions = options.CreateParseOptions();
        var codeRevision = CodeFiles.Revision;
        var codeTrees = CodeFiles.Snapshot.Values.OrderBy(d => d.Path, StringComparer.Ordinal)
            .Select(d => CSharpSyntaxTree.ParseText(d.Text, parseOptions, d.Path, cancellationToken: cancellationToken))
            .Prepend(CSharpSyntaxTree.ParseText(code, parseOptions, "Code.cs", cancellationToken: cancellationToken)).ToArray();
        var references = options.ReferenceNames == null ? _references : _references.Where(reference => options.ReferenceNames.Contains(reference.Display!, StringComparer.Ordinal)).ToImmutableArray();
        var compilation = CSharpCompilation.Create(name, codeTrees, references, options.CreateCompilationOptions());
        var profile = KnownFrameworkProfiles.Select(compilation, framework, createSourceInfo: true);
        var resourceRevision = Resources.Revision;
        var inputs = (resourceDocuments ?? Resources.Snapshot.Values.ToArray()).Select(s => new XamlProjectDocument(s, s.Path))
            .Prepend(new XamlProjectDocument(syntax, "View.axaml")).ToArray();
        var authoring = new XamlCompilationSession(compilation, profile, projectDocuments: inputs);
        var project = authoring.CompileProject(inputs.Select(d => d.Syntax), cancellationToken);
        var main = project.Documents.Single(d => d.Input.LogicalPath == "View.axaml");
        var analysis = new XamlAnalysis(syntax, main.Document, main.Output);
        var diagnostics = ImmutableArray.CreateBuilder<PlaygroundDiagnostic>();
        foreach (var document in project.Documents)
        {
            var text = document.Input.Syntax;
            foreach (var item in document.Output.Diagnostics)
            {
                var start = text.Lines.GetPosition(Math.Min(item.Span.Start, text.Text.Length));
                var end = text.Lines.GetPosition(Math.Min(item.Span.End, text.Text.Length));
                diagnostics.Add(new(item.Code, item.Message, item.Severity.ToString(), text.Path, start.Line + 1, start.Character + 1, end.Line + 1, end.Character + 1));
            }
        }
        compilation = XamlCSharpCompilation.AddGeneratedSources(compilation, project, parseOptions, cancellationToken);
        foreach (var item in project.SourceIntegration.Diagnostics.Concat(compilation.GetDiagnostics(cancellationToken)))
        {
            var location = item.Location.GetMappedLineSpan();
            diagnostics.Add(new(item.Id, item.GetMessage(), item.Severity.ToString(), location.Path,
                location.StartLinePosition.Line + 1, location.StartLinePosition.Character + 1,
                location.EndLinePosition.Line + 1, location.EndLinePosition.Character + 1, item.IsSuppressed, item.IsWarningAsError, item.DefaultSeverity.ToString()));
        }
        clock.Stop();
        return new(analysis, compilation, diagnostics.ToImmutable(), clock.Elapsed.TotalMilliseconds)
        { Project = project, ResourceRevision = resourceRevision, CodeRevision = codeRevision, SettingsRevision = settingsRevision, Settings = options, CodeText = code,
            SourcePaths = codeTrees.Select(t => t.FilePath).ToImmutableHashSet(StringComparer.Ordinal), AuthoringCompiler = authoring };
    }

    public object Run(BrowserCompilation result)
    {
        if (!result.Success) throw new InvalidOperationException("Resolve the compiler errors before running this project.");
        EnsureBrowserRunnable(result);
        if (_loadedAssemblies >= 64) throw new InvalidOperationException("This tab has loaded 64 preview assemblies. Export the project and reload to reclaim the runtime.");
        using var image = new MemoryStream();
        var emitted = result.Compilation.Emit(image);
        if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var assembly = Assembly.Load(image.ToArray()); _loadedAssemblies++;
        var output = result.Analysis.Output;
        var factory = assembly.GetType(output.FactoryMetadataName, throwOnError: true)!;
        if (output.BuildMethodName != null) return factory.GetMethod(output.BuildMethodName)!.Invoke(null, new object?[] { null })!;
        var instance = Activator.CreateInstance(factory) ?? throw new InvalidOperationException("The code-behind root could not be constructed.");
        factory.GetMethod(output.PopulateMethodName)!.Invoke(null, new object?[] { instance, null });
        return instance;
    }
    public static void EnsureBrowserRunnable(BrowserCompilation result)
    {
        if (result.Compilation.Options.OutputKind != OutputKind.DynamicallyLinkedLibrary || result.Compilation.Options.Platform != Platform.AnyCpu)
            throw new InvalidOperationException("The browser preview requires DynamicallyLinkedLibrary output and AnyCpu. Other targets can be compiled and exported.");
    }
}
