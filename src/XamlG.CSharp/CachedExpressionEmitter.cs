using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Shares statically typed immutable framework values without construction-frame captures.</summary>
internal sealed class CachedExpressionEmitter(EmissionContext context)
{
    private readonly Dictionary<(string Key, string Type), (string Name, BoundExpression Value)> _values = new();
    private readonly List<(string Name, BoundExpression Value)> _ordered = new();
    private readonly Dictionary<(string Key, string Type), string> _shared = new();
    private readonly Dictionary<string, SharedGeneratedSource> _sources = new(StringComparer.Ordinal);
    public IEnumerable<SharedGeneratedSource> SharedSources => _sources.Values;

    public string Get(BoundCachedExpression expression)
    {
        if (expression.Type?.IsReferenceType != true || ValueEmitter.UsesFrame(expression.Value))
        {
            context.Error("A cached value must be a frame-independent immutable reference.", expression.Span);
            return "default!";
        }
        var key = (expression.Key, expression.Type.CSharpName());
        if (context.ShareCachedValues && expression.ShareAcrossDocuments)
        {
            if (!_shared.TryGetValue(key, out var reference))
            {
                // A separate, deterministic body gives every document the same
                // helper identity. Project publication already deduplicates these
                // sources and moves their owner after incremental document edits.
                using var isolated = new EmissionContext(context.Document with
                { Options = context.Document.Options with { DocumentId = expression.Key }, Diagnostics = [] }, context.Cancellation);
                isolated.ShareCachedValues = true;
                var objects = new ObjectEmitter(isolated);
                var values = new ValueEmitter(isolated, objects);
                var result = values.Emit(expression.Value, string.Empty);
                var statements = isolated.Writer.ToString();
                isolated.CachedExpressions.EmitHelpers(objects);
                isolated.EmitMetadataHelpers();
                context.Diagnostics.AddRange(isolated.Diagnostics);
                var body = isolated.Writer.ToString();
                var id = context.StableId(expression.Key + "\0" + body + "\0" + result);
                var method = "Get_" + id;
                var field = "_value_" + id;
                var ns = context.Document.Options.GeneratedNamespace;
                var typeName = "global::" + ns + ".__XamlGCachedValues";
                var valueType = expression.Type.CSharpName();
                var writer = new CSharpWriter();
                writer.Line("#nullable enable annotations"); writer.Line("#nullable disable warnings");
                writer.Open("namespace " + ns); writer.Open("internal static partial class __XamlGCachedValues");
                writer.Line("private static " + valueType + "? " + field + ";");
                writer.Open("internal static " + valueType + " " + method + "()");
                writer.Line("if (" + field + " is { } __cached) return __cached;");
                writer.Line(statements);
                writer.Line("return " + field + " = " + result + ";");
                writer.Close();
                writer.Line(body.Substring(statements.Length));
                writer.Close(); writer.Close();
                reference = typeName + "." + method;
                var shared = new SharedGeneratedSource(typeName, writer.ToString()) { Identity = reference };
                _shared.Add(key, reference);
                foreach (var dependency in isolated.CachedExpressions.SharedSources) _sources[dependency.Identity] = dependency;
                _sources[shared.Identity] = shared;
            }
            return reference + "()";
        }
        if (!_values.TryGetValue(key, out var cached))
        {
            cached = ("__XamlGCached_" + context.Id + "_" + _values.Count, expression.Value);
            _values.Add(key, cached);
            _ordered.Add(cached);
        }
        return cached.Name + "()";
    }

    public void EmitHelpers(ObjectEmitter objects)
    {
        var values = new ValueEmitter(context, objects);
        // Nested cached values can register helpers while their owners are emitted.
        for (var index = 0; index < _ordered.Count; index++)
        {
            var cached = _ordered[index];
            EmitValue(context.Writer, values, cached.Value, cached.Name, "private");
        }
    }

    private static void EmitValue(CSharpWriter writer, ValueEmitter values, BoundExpression expression, string name, string accessibility)
    {
        var type = expression.Type!.CSharpName();
        var field = name + "_value";
        writer.Line("private static " + type + "? " + field + ";");
        writer.Open(accessibility + " static " + type + " " + name + "()");
        writer.Line("if (" + field + " is { } __cached) return __cached;");
        var value = values.Emit(expression, string.Empty);
        writer.Line("return " + field + " = " + value + ";");
        writer.Close();
    }
}
