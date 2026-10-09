using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace XamlG.IntelligentUI;

public sealed record UiCSharpExecutionRequest(string Sha256, string Source, string GeneratedSource,
    ImmutableArray<string> References, string Warning = "Full C# executes application code with the embedding process's authority. AssemblyLoadContext is not a security sandbox.");

/// <summary>Opt-in full Roslyn C# expressions and statement bodies, including local functions,
/// patterns, LINQ and referenced application types. The owner must approve each exact source
/// before it can be loaded. Use an isolated process/frame for untrusted application code.
/// Unlike the default pure evaluator, this backend cannot enforce CPU/memory or side-effect limits.</summary>
public sealed class UiCSharpExpressionCompiler : IUiExpressionCompiler, IDisposable
{
    private readonly object _gate = new();
    private readonly ImmutableArray<MetadataReference> _references;
    private readonly Func<UiCSharpExecutionRequest, bool> _approve;
    private readonly Dictionary<string, Compiled> _cache = new(StringComparer.Ordinal);
    private readonly int _maximumCompilations;
    private bool _disposed;
    public string Language => "csharp-full";
    public UiCSharpExpressionCompiler(IEnumerable<MetadataReference> references, Func<UiCSharpExecutionRequest, bool> approveExecution, int maximumCompilations = 64)
    {
        ArgumentNullException.ThrowIfNull(references); ArgumentNullException.ThrowIfNull(approveExecution);
        if (maximumCompilations is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumCompilations));
        _references = references.ToImmutableArray();
        if (_references.IsEmpty || _references.Length > 1024) throw new ArgumentException("Supply the application's bounded metadata reference set.", nameof(references));
        _approve = approveExecution; _maximumCompilations = maximumCompilations;
    }
    public UiCSharpExecutionRequest Describe(string source, UiLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source); limits ??= new(); limits.Validate();
        if (source.Length is 0 || source.Length > limits.ExpressionCharacters) throw new UiException("expression_limit", "Full C# source exceeds the configured expression limit.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        var sourceLiteral = SymbolDisplay.FormatLiteral(source, quote: true);
        // The generated method returns object so a statement body can use ordinary C# returns.
        // Serialization is outside the user body and uses the configured JSON byte bound.
        var body = source.TrimStart().StartsWith('{') ? source : "{ return (" + source + "); }";
        var generated = "#nullable enable\nusing System;\nusing System.Collections.Generic;\nusing System.Linq;\nusing System.Text;\nusing System.Text.Json;\nusing System.Globalization;\n" +
            "namespace XamlG.GeneratedUI; public sealed class Expression : global::XamlG.IntelligentUI.IUiExpression {\n" +
            "public string Source => " + sourceLiteral + ";\n" +
            "public JsonElement Evaluate(JsonElement state, JsonElement data, JsonElement? item = null) => JsonSerializer.SerializeToElement(Execute(state, data, item));\n" +
            "private static object? Execute(JsonElement state, JsonElement data, JsonElement? item)\n" + body + "\n}\n";
        return new(hash, source, generated, _references.Select(reference => reference.Display ?? "in-memory metadata").ToImmutableArray());
    }
    public IUiExpression Compile(string source, UiLimits limits)
    {
        var request = Describe(source, limits);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Never let a cached compilation bypass a revoked or changed owner approval.
            if (!_approve(request)) throw new UiException("execution_not_approved", "Full C# execution requires owner approval for source SHA-256 " + request.Sha256);
            if (_cache.TryGetValue(request.Sha256, out var found)) return new BoundedResult(found.Expression, limits.DataBytes);
            if (_cache.Count >= _maximumCompilations) throw new UiException("compilation_limit", "The full C# host reached its compilation limit. Retire this host before compiling another source.");
            var tree = CSharpSyntaxTree.ParseText(request.GeneratedSource, new CSharpParseOptions(LanguageVersion.Preview), path: "IntelligentExpression.cs", encoding: Encoding.UTF8);
            var compilation = CSharpCompilation.Create("XamlG.UI." + request.Sha256 + "." + Guid.NewGuid().ToString("N"), [tree], _references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable, deterministic: true));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success)
                throw new UiException("csharp_compilation_failed", string.Join("\n", emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(32).Select(diagnostic => diagnostic.ToString())));
            if (image.Length > 4 * 1024 * 1024) throw new UiException("compilation_limit", "Generated expression assembly exceeds 4 MiB.");
            image.Position = 0;
            ExpressionLoadContext? context = null;
            try
            {
                Assembly assembly;
                if (OperatingSystem.IsBrowser()) assembly = Assembly.Load(image.ToArray());
                else { context = new ExpressionLoadContext(); assembly = context.LoadFromStream(image); }
                var type = assembly.GetType("XamlG.GeneratedUI.Expression", throwOnError: true)!;
                var expression = (IUiExpression)Activator.CreateInstance(type)!;
                _cache.Add(request.Sha256, new(expression, context));
                return new BoundedResult(expression, limits.DataBytes);
            }
            catch { context?.Unload(); throw; }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var compiled in _cache.Values) compiled.Context?.Unload();
            _cache.Clear();
        }
    }
    private sealed record Compiled(IUiExpression Expression, ExpressionLoadContext? Context);
    private sealed class BoundedResult(IUiExpression expression, int maximumBytes) : IUiExpression
    {
        public string Source => expression.Source;
        public JsonElement Evaluate(JsonElement state, JsonElement data, JsonElement? item = null)
        {
            var value = expression.Evaluate(state, data, item); UiDataStore.ValidateJson(value, maximumBytes); return value.Clone();
        }
    }
    private sealed class ExpressionLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
    }
}
