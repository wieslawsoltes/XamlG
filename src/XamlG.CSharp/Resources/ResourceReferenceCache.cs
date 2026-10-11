using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using XamlG.Compiler;
using XamlG.Compiler.Resources;

namespace XamlG.CSharp.Resources;

/// <summary>Only the first reference per ordinal dependency URI is cached, never
/// graph validity or emitted-factory availability. This is not an occurrence list.
/// Diagnostic-only document copies may share a root.</summary>
internal static class ResourceReferenceCache
{
    private sealed record Snapshot(ImmutableArray<BoundResourceExpression> Values);
    private static readonly Snapshot Empty = new(ImmutableArray<BoundResourceExpression>.Empty);
    private static readonly ConditionalWeakTable<BoundObject, Snapshot> Roots = new();

    public static ImmutableArray<BoundResourceExpression> Get(BoundDocument document, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (document.Root is not { } root) return Empty.Values;
        // Empty immutable roots cannot acquire dependencies. Avoid retaining even
        // a weak-table slot for them; preserve the final cancellation observation.
        if (root.Arguments.IsEmpty && root.Assignments.IsEmpty)
        {
            cancellation.ThrowIfCancellationRequested();
            return Empty.Values;
        }
        if (!Roots.TryGetValue(root, out var snapshot))
            snapshot = GetOrCreate(root, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return snapshot.Values;
    }

    private static Snapshot GetOrCreate(BoundObject root, CancellationToken cancellation) =>
        cancellation.CanBeCanceled ? GetCancelable(root, cancellation) :
            Roots.GetValue(root, static value => Scan(value, default));

    private static Snapshot GetCancelable(BoundObject root, CancellationToken cancellation) =>
        // Keep this capture in a separate cold method. Neither a cache hit nor
        // an uncancelable miss should allocate its display class or delegate.
        Roots.GetValue(root, value => Scan(value, cancellation));

    private static Snapshot Scan(BoundObject root, CancellationToken cancellation)
    {
        var resources = ResourceDependencyScanner.Scan(root, cancellation);
        // A successful cold scan is complete before publication. Cancelled or
        // failed scans never publish partial URI/visited state into the weak cache.
        return resources.IsEmpty ? Empty : new(resources);
    }
}
