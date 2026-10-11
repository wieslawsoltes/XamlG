using System.Collections.Immutable;
using System.Threading;

namespace XamlG.CSharp.Resources;

/// <summary>Publishes each helper at its first successful document occurrence.
/// Stable cached ownership requires no temporary selected-source array or identity string.</summary>
internal static class SharedSourcePublisher
{
    public static void Publish(XamlEmissionResult[] emissions, CachedProjectDocument[] entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<string>? published = null;
        for (var i = 0; i < emissions.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var emission = emissions[i];
            if (!emission.Success || emission.SharedSources.IsEmpty) continue;
            var identities = published ??= new(StringComparer.Ordinal);
            var entry = ReferenceEquals(emission, entries[i].Output) ? entries[i] : null;
            var previous = entry?.PublishedSources ?? ImmutableArray<SharedGeneratedSource>.Empty;
            ImmutableArray<SharedGeneratedSource>.Builder? changed = null;
            var count = 0;
            foreach (var source in emission.SharedSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!identities.Add(source.Identity)) continue;
                if (changed != null) changed.Add(source);
                else if (count >= previous.Length || !ReferenceEquals(previous[count], source))
                {
                    changed = Prefix(previous, count);
                    changed.Add(source);
                }
                count++;
            }
            // Retain the remembered publication when this document temporarily
            // loses every helper; it can be reused if an earlier owner disappears.
            if (count == 0) continue;
            if (changed == null && count == previous.Length && entry?.OutputWithHelpers is { } cached)
            {
                emissions[i] = cached;
                continue;
            }
            // A shortened matching prefix is also an ownership change. Snapshot
            // only on a miss; never retain the mutable published-set or caller arrays.
            var owned = (changed ?? Prefix(previous, count)).ToImmutable();
            var output = emission with
            { Source = emission.Source + "\n" + string.Join("\n", owned.Select(source => source.Source)) };
            cancellationToken.ThrowIfCancellationRequested();
            if (entry != null)
            {
                entry.PublishedSources = owned;
                entry.OutputWithHelpers = output;
            }
            emissions[i] = output;
        }
    }

    private static ImmutableArray<SharedGeneratedSource>.Builder Prefix(ImmutableArray<SharedGeneratedSource> previous, int count)
    {
        var result = ImmutableArray.CreateBuilder<SharedGeneratedSource>(Math.Max(4, count));
        for (var i = 0; i < count; i++) result.Add(previous[i]);
        return result;
    }
}
