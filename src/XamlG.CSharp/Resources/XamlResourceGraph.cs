using System.Collections.Immutable;
using System.Threading;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

/// <summary>Iterative dependency validation with O(V + E) work and no per-document dependency sets.</summary>
internal static class XamlResourceGraph
{
    public static BoundDocument[] Validate(BoundDocument[] documents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var local = LocalDocuments(documents, cancellationToken);
        if (local == null) return documents;
        var reverse = ReverseEdges(documents, local, countDependencies: true, cancellationToken, out var remaining);
        if (reverse == null) return documents;
        var queue = new Queue<int>();
        for (var i = 0; i < documents.Length; i++)
            if (remaining![i] == 0) queue.Enqueue(i);
        var visited = new bool[documents.Length];
        while (queue.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = queue.Dequeue();
            visited[index] = true;
            if (reverse[index] is not { } parents) continue;
            foreach (var incoming in parents)
                if (--remaining![incoming.Parent] == 0) queue.Enqueue(incoming.Parent);
        }
        for (var i = 0; i < documents.Length; i++)
        {
            if (visited[i]) continue;
            // Reference snapshots retain the old traversal order. Select precisely
            // the first unresolved local edge, including documents leading into a cycle.
            foreach (var edge in References(documents[i], cancellationToken))
            {
                if (!local.TryGetValue(edge.Resource.Uri, out var target) || target < 0 || visited[target]) continue;
                documents[i] = Error(documents[i], "XG3304", "This document depends on a compiled-resource cycle through '" + edge.Resource.Uri + "'.", edge.Span);
                break;
            }
        }
        // Reuse the exhausted queue and visited storage for failure propagation.
        // Seed in document order, as before: it determines the first diagnostic when
        // a caller includes more than one independently failed resource.
        for (var i = 0; i < documents.Length; i++)
        {
            visited[i] = !documents[i].Success;
            if (visited[i]) queue.Enqueue(i);
        }
        while (queue.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reverse[queue.Dequeue()] is not { } parents) continue;
            foreach (var incoming in parents)
            {
                if (visited[incoming.Parent]) continue;
                visited[incoming.Parent] = true;
                documents[incoming.Parent] = Error(documents[incoming.Parent], "XG3305",
                    "The included document failed compilation: " + incoming.Edge.Resource.Uri, incoming.Edge.Span);
                queue.Enqueue(incoming.Parent);
            }
        }
        return documents;
    }

    /// <summary>Backend-only failures suppress the complete caller closure without
    /// mutating cached raw bindings, reusable emissions or shared helper ownership.</summary>
    public static void ValidateEmissions(BoundDocument[] documents, XamlEmissionResult[] outputs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!outputs.Any(output => !output.Success)) return;
        var local = LocalDocuments(documents, cancellationToken);
        if (local == null) return;
        var reverse = ReverseEdges(documents, local, countDependencies: false, cancellationToken, out _);
        if (reverse == null) return;
        var queue = new Queue<int>();
        for (var i = 0; i < outputs.Length; i++)
            if (!outputs[i].Success) queue.Enqueue(i);
        while (queue.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reverse[queue.Dequeue()] is not { } parents) continue;
            foreach (var incoming in parents)
            {
                var parent = incoming.Parent;
                if (!outputs[parent].Success) continue;
                var edge = incoming.Edge;
                var diagnostic = new XamlDiagnostic("XG3305", "The included document failed code generation: " + edge.Resource.Uri, edge.Span);
                documents[parent] = documents[parent] with { Diagnostics = documents[parent].Diagnostics.Add(diagnostic) };
                outputs[parent] = outputs[parent] with
                {
                    Source = string.Empty, SourceMappings = ImmutableArray<XamlSourceMapping>.Empty,
                    Diagnostics = outputs[parent].Diagnostics.Add(diagnostic)
                };
                queue.Enqueue(parent);
            }
        }
    }

    private static Dictionary<string, int>? LocalDocuments(BoundDocument[] documents, CancellationToken cancellation)
    {
        Dictionary<string, int>? local = null;
        var unique = 0;
        for (var i = 0; i < documents.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (documents[i].Options.ResourceUri is not { } uri) continue;
            local ??= new(StringComparer.Ordinal);
            if (!local.TryGetValue(uri, out var previous)) { local.Add(uri, i); unique++; }
            else if (previous >= 0) { local[uri] = -1; unique--; }
        }
        // Negative slots permanently mark ambiguous URIs, including three or more
        // declarations. Never silently choose the last duplicate as a valid target.
        return unique == 0 ? null : local;
    }

    private static List<Incoming>?[]? ReverseEdges(BoundDocument[] documents, Dictionary<string, int> local,
        bool countDependencies, CancellationToken cancellation, out int[]? remaining)
    {
        List<Incoming>?[]? reverse = null;
        int[]? seenParents = null;
        remaining = null;
        for (var parent = 0; parent < documents.Length; parent++)
        {
            foreach (var edge in References(documents[parent], cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!local.TryGetValue(edge.Resource.Uri, out var target) || target < 0) continue;
                if (reverse == null)
                {
                    reverse = new List<Incoming>?[documents.Length];
                    seenParents = new int[documents.Length];
                    if (countDependencies) remaining = new int[documents.Length];
                }
                // One dense stamp array replaces a HashSet per caller. Mark the
                // parent with +1 because zero is the array's unvisited value. Keep
                // its first edge/span; duplicate includes never add dependency counts.
                if (seenParents![target] == parent + 1) continue;
                seenParents[target] = parent + 1;
                (reverse[target] ??= new()).Add(new(parent, edge));
                if (remaining != null) remaining[parent]++;
            }
        }
        return reverse;
    }

    private readonly struct Incoming(int parent, BoundResourceExpression edge)
    {
        public int Parent { get; } = parent;
        public BoundResourceExpression Edge { get; } = edge;
    }

    internal static ImmutableArray<BoundResourceExpression> References(BoundDocument document, CancellationToken cancellationToken = default) =>
        ResourceReferenceCache.Get(document, cancellationToken);
    private static BoundDocument Error(BoundDocument document, string code, string message, TextSpan span) =>
        document with { Diagnostics = document.Diagnostics.Add(new XamlDiagnostic(code, message, span)) };
}
