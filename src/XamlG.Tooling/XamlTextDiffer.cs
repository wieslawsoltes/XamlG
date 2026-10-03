using System.Collections.Immutable;
using System.Threading;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>Converts a full editor buffer into one minimal replacement, retaining unchanged
/// source outside the edit and avoiding boundaries inside UTF-16 surrogate pairs.</summary>
public static class XamlTextDiffer
{
    public static ImmutableArray<XamlTextChange> GetChanges(string previous, string current,
        CancellationToken cancellationToken = default)
    {
        if (previous == null) throw new ArgumentNullException(nameof(previous));
        if (current == null) throw new ArgumentNullException(nameof(current));
        cancellationToken.ThrowIfCancellationRequested();
        var start = 0;
        var limit = Math.Min(previous.Length, current.Length);
        while (start < limit && previous[start] == current[start])
        {
            if ((start & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            start++;
        }
        if (start == previous.Length && start == current.Length) return ImmutableArray<XamlTextChange>.Empty;
        if (InsideSurrogatePair(previous, start) || InsideSurrogatePair(current, start)) start--;
        var oldEnd = previous.Length;
        var newEnd = current.Length;
        while (oldEnd > start && newEnd > start && previous[oldEnd - 1] == current[newEnd - 1])
        {
            if ((oldEnd & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            oldEnd--;
            newEnd--;
        }
        if (InsideSurrogatePair(previous, oldEnd) || InsideSurrogatePair(current, newEnd))
        {
            oldEnd++;
            newEnd++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ImmutableArray.Create(new XamlTextChange(new(start, oldEnd - start), current.Substring(start, newEnd - start)));
    }

    private static bool InsideSurrogatePair(string text, int index) => index > 0 && index < text.Length &&
        char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]);
}
