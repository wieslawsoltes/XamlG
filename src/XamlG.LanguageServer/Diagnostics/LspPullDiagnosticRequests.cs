using System.Collections.Immutable;
using System.Text.Json;
using XamlG.Compiler.Resources;
using XamlG.Tooling;
using XamlG.Tooling.Refactoring;

namespace XamlG.LanguageServer.Diagnostics;

/// <summary>Projects the same coherent analysis used by semantic requests. Includes loaded closed
/// documents and explicit empty reports for documents removed since the client's previous workspace pull.</summary>
internal sealed class LspPullDiagnosticRequests(LspDiagnosticCache cache, bool relatedDocuments)
{
    private const int MaximumPreviousResults = 16384;
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public object Handle(string method, JsonElement parameters, LspWorkspaceAnalysis workspace,
        LspDocumentSetSnapshot buffers, CancellationToken token)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new LspRequestException(-32602, "Diagnostic parameters must be an object.");
        if (parameters.TryGetProperty("identifier", out _) && ReadString(parameters, "identifier", 256) != LspDiagnosticMethods.Identifier)
            throw new LspRequestException(-32602, "Unknown diagnostic provider identifier.");
        var byUri = workspace.Documents.ToDictionary(a => UriFor(a, buffers), StringComparer.Ordinal);
        if (method == LspDiagnosticMethods.Document)
        {
            var uri = ReadString(parameters.GetProperty("textDocument"), "uri", 8192);
            ValidateUri(uri);
            var selected = byUri.TryGetValue(uri, out var exact) ? exact : workspace.Documents.FirstOrDefault(a => SamePath(a.Syntax.Path, uri));
            if (selected == null) throw new LspRequestException(-32602, "The diagnostic document is not part of the loaded XAML workspace.");
            var previous = parameters.TryGetProperty("previousResultId", out _) ? ReadString(parameters, "previousResultId", 256, allowEmpty: true) : null;
            var report = cache.Report(uri, LspDiagnosticProjection.Create(selected, token), previous, token);
            if (relatedDocuments)
            {
                var related = ImmutableDictionary.CreateBuilder<string, LspDocumentDiagnosticReport>(StringComparer.Ordinal);
                foreach (var other in Related(selected, workspace.Documents, token))
                {
                    var otherUri = UriFor(other, buffers);
                    related.Add(otherUri, cache.Report(otherUri, LspDiagnosticProjection.Create(other, token), cancellationToken: token));
                }
                if (related.Count != 0) report = report with { RelatedDocuments = related.ToImmutable() };
            }
            return report;
        }
        if (method != LspDiagnosticMethods.Workspace) throw new LspRequestException(-32601, "Unknown pull diagnostic method.");
        var previousResults = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameters.TryGetProperty("previousResultIds", out var previousItems))
        {
            if (previousItems.ValueKind != JsonValueKind.Array || previousItems.GetArrayLength() > MaximumPreviousResults)
                throw new LspRequestException(-32602, "The previous diagnostic result list exceeds its limit or is not an array.");
            foreach (var item in previousItems.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var uri = ReadString(item, "uri", 8192); ValidateUri(uri);
                if (!previousResults.TryAdd(uri, ReadString(item, "value", 256, allowEmpty: true))) throw new LspRequestException(-32602, "Duplicate previous diagnostic document URI.");
            }
        }
        var reports = new List<LspWorkspaceDocumentDiagnosticReport>();
        foreach (var document in byUri.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            previousResults.Remove(document.Key, out var previous);
            var result = cache.Report(document.Key, LspDiagnosticProjection.Create(document.Value, token), previous, token);
            var version = buffers.Documents.FirstOrDefault(d => Paths.Equals(d.Syntax.Path, document.Value.Syntax.Path))?.Version;
            reports.Add(new(document.Key, version, result.Kind, result.ResultId) { Items = result.Items });
        }
        // This is a clear operation, not an attempt to read/analyze client-provided paths.
        foreach (var removed in previousResults.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var report = cache.Report(removed.Key, ImmutableArray<LspDiagnosticItem>.Empty, removed.Value, token);
            reports.Add(new(removed.Key, null, report.Kind, report.ResultId) { Items = report.Items });
        }
        return new { items = reports.ToArray() };
    }
    private static IEnumerable<XamlAnalysis> Related(XamlAnalysis selected, ImmutableArray<XamlAnalysis> workspace, CancellationToken token)
    {
        var byResource = workspace.Where(a => a.Document.Options.ResourceUri != null)
            .GroupBy(a => a.Document.Options.ResourceUri!, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var edges = workspace.ToDictionary(a => a, _ => new HashSet<XamlAnalysis>());
        foreach (var analysis in workspace)
        {
            token.ThrowIfCancellationRequested();
            foreach (var resource in BoundDocumentTraversal.Expressions(analysis.Document).OfType<BoundResourceExpression>())
                if (byResource.TryGetValue(resource.Resource.Uri, out var target))
                { edges[analysis].Add(target); edges[target].Add(analysis); }
        }
        var visited = new HashSet<XamlAnalysis> { selected }; var queue = new Queue<XamlAnalysis>(); queue.Enqueue(selected);
        while (queue.Count != 0 && visited.Count <= 256)
        {
            token.ThrowIfCancellationRequested();
            foreach (var target in edges[queue.Dequeue()].OrderBy(a => a.Syntax.Path, StringComparer.Ordinal))
            {
                if (!visited.Add(target)) continue;
                if (visited.Count > 256) yield break;
                yield return target; queue.Enqueue(target);
            }
        }
    }
    private static string UriFor(XamlAnalysis analysis, LspDocumentSetSnapshot buffers) =>
        buffers.Documents.FirstOrDefault(d => Paths.Equals(d.Syntax.Path, analysis.Syntax.Path))?.Uri ?? LspConversions.UriForPath(analysis.Syntax.Path);
    private static bool SamePath(string path, string uri) => Paths.Equals(path, new Uri(uri).IsFile ? new Uri(uri).LocalPath : uri);
    private static void ValidateUri(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("file" or "untitled") || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new LspRequestException(-32602, "Only file/untitled diagnostic URIs without query or fragment are accepted.");
    }
    private static string ReadString(JsonElement parent, string property, int maximum, bool allowEmpty = false)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.String ||
            node.GetString() is not { } value || (!allowEmpty && value.Length == 0) || value.Length > maximum)
            throw new LspRequestException(-32602, "Invalid or oversized diagnostic parameter: " + property);
        return value;
    }
}
