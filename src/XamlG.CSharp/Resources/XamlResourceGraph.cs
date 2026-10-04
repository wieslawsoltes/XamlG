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
