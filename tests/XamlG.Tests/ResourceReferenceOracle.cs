using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp;

namespace XamlG.Tests;

/// <summary>Frozen occurrence-expanding scan from main 1fcd01d. Do not forward
/// this oracle to the optimized dependency cache; that would hide discovery bugs.</summary>
internal static class ResourceReferenceOracle
{
    public static ImmutableArray<BoundResourceExpression> Scan(BoundDocument document, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (document.Root is not { } root) return ImmutableArray<BoundResourceExpression>.Empty;
        ImmutableArray<BoundResourceExpression>.Builder? resources = null;
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
        return resources?.ToImmutable() ?? ImmutableArray<BoundResourceExpression>.Empty;
    }
}
