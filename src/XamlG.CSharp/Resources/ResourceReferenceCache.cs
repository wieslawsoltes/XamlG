using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using XamlG.Compiler;
using XamlG.Compiler.Resources;

namespace XamlG.CSharp.Resources;

/// <summary>Only bound expression references are cached, never graph validity or
/// emitted-factory availability. Diagnostic-only document copies may share a root.</summary>
internal static class ResourceReferenceCache
{
    private sealed record Snapshot(ImmutableArray<BoundResourceExpression> Values);
    private static readonly Snapshot Empty = new(ImmutableArray<BoundResourceExpression>.Empty);
    private static readonly ConditionalWeakTable<BoundObject, Snapshot> Roots = new();

    public static ImmutableArray<BoundResourceExpression> Get(BoundDocument document, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (document.Root is not { } root) return Empty.Values;
        if (!Roots.TryGetValue(root, out var snapshot))
            snapshot = Roots.GetValue(root, value => Scan(value, cancellation));
        cancellation.ThrowIfCancellationRequested();
        return snapshot.Values;
    }

    private static Snapshot Scan(BoundObject root, CancellationToken cancellation)
    {
        ImmutableArray<BoundResourceExpression>.Builder? resources = null;
        // Preserve the historical reverse-expression traversal and thus the exact
        // first failing edge/span selected by cycle and error diagnostics.
        var stack = new Stack<BoundExpression>(BoundTraversal.Expressions(root));
        while (stack.Count != 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var expression = stack.Pop();
            if (expression is BoundResourceExpression resource)
                (resources ??= ImmutableArray.CreateBuilder<BoundResourceExpression>()).Add(resource);
            foreach (var child in BoundTraversal.Children(expression, true)) stack.Push(child);
        }
        cancellation.ThrowIfCancellationRequested();
        return resources == null ? Empty : new(resources.ToImmutable());
    }
}
