using System.Collections.Immutable;
using System.Threading;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

/// <summary>Dependency validation is iterative and linear in vertices and edges, including documents depending on a cycle.</summary>
internal static class XamlResourceGraph
{
    public static BoundDocument[] Validate(BoundDocument[] documents, CancellationToken cancellationToken)
    {
        var local = documents.Select((d, i) => (Document: d, Index: i)).Where(p => p.Document.Options.ResourceUri != null)
            .GroupBy(p => p.Document.Options.ResourceUri!, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Index, StringComparer.Ordinal);
        var edges = documents.Select(d => References(d).Where(r => local.ContainsKey(r.Resource.Uri)).ToArray()).ToArray();
        var dependencies = edges.Select(list => list.Select(r => local[r.Resource.Uri]).Distinct().ToHashSet()).ToArray();
        var reverse = Enumerable.Range(0, documents.Length).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < dependencies.Length; i++) foreach (var target in dependencies[i]) reverse[target].Add(i);
        var queue = new Queue<int>(Enumerable.Range(0, documents.Length).Where(i => dependencies[i].Count == 0));
        var visited = new bool[documents.Length];
        while (queue.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = queue.Dequeue(); visited[index] = true;
            foreach (var parent in reverse[index]) if (dependencies[parent].Remove(index) && dependencies[parent].Count == 0) queue.Enqueue(parent);
        }
        for (var i = 0; i < documents.Length; i++)
        {
            if (visited[i]) continue;
            var edge = edges[i].First(r => !visited[local[r.Resource.Uri]]);
            documents[i] = Error(documents[i], "XG3304", "This document depends on a compiled-resource cycle through '" + edge.Resource.Uri + "'.", edge.Span);
        }
        queue = new(Enumerable.Range(0, documents.Length).Where(i => !documents[i].Success));
        var reported = new HashSet<int>(queue);
        while (queue.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failed = queue.Dequeue();
            foreach (var parent in reverse[failed])
            {
                if (!reported.Add(parent)) continue;
                var edge = edges[parent].First(r => local[r.Resource.Uri] == failed);
                documents[parent] = Error(documents[parent], "XG3305", "The included document failed compilation: " + edge.Resource.Uri, edge.Span);
                queue.Enqueue(parent);
            }
        }
        return documents;
    }
    /// <summary>Backend-only failures can remove a factory after binding succeeded. Suppress the
    /// complete caller closure without changing cached raw bindings or reusable emissions.</summary>
    public static void ValidateEmissions(BoundDocument[] documents, XamlEmissionResult[] outputs, CancellationToken cancellationToken)
    {
        if (!outputs.Any(output => !output.Success)) return;
        var local = documents.Select((document, index) => (Document: document, Index: index))
            .Where(pair => pair.Document.Options.ResourceUri != null)
            .GroupBy(pair => pair.Document.Options.ResourceUri!, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Index, StringComparer.Ordinal);
        var reverse = Enumerable.Range(0, documents.Length).Select(_ => new List<(int Parent, BoundResourceExpression Edge)>()).ToArray();
        for (var i = 0; i < documents.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var edge in References(documents[i]))
                if (local.TryGetValue(edge.Resource.Uri, out var target)) reverse[target].Add((i, edge));
        }
        var queue = new Queue<int>(Enumerable.Range(0, outputs.Length).Where(i => !outputs[i].Success));
        while (queue.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (parent, edge) in reverse[queue.Dequeue()])
            {
                if (!outputs[parent].Success) continue;
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
    internal static IEnumerable<BoundResourceExpression> References(BoundDocument document)
    {
        if (document.Root == null) yield break;
        var stack = new Stack<BoundExpression>(BoundTraversal.Expressions(document.Root));
        while (stack.Count != 0)
        {
            var expression = stack.Pop();
            if (expression is BoundResourceExpression resource) yield return resource;
            foreach (var child in BoundTraversal.Children(expression, true)) stack.Push(child);
        }
    }
    private static BoundDocument Error(BoundDocument document, string code, string message, TextSpan span) =>
        document with { Diagnostics = document.Diagnostics.Add(new XamlDiagnostic(code, message, span)) };
}
