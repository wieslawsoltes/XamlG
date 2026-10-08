using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

internal sealed class CSharpRenamePositions
{
    private readonly Dictionary<string, (int[] Ends, int[] Deltas)> _paths = new(StringComparer.Ordinal);
    internal CSharpRenamePositions(ImmutableArray<XamlDocumentEdits> documents)
    {
        foreach (var document in documents)
        {
            var changes = document.Changes.OrderBy(change => change.Span.Start).ToArray();
            var ends = new int[changes.Length]; var deltas = new int[changes.Length]; var delta = 0; var previous = -1;
            for (var index = 0; index < changes.Length; index++)
            {
                var change = changes[index];
                if (change.Span.Start < previous || index != 0 && change.Span.Start == changes[index - 1].Span.Start)
                    throw new InvalidOperationException("Rename references produced overlapping source edits.");
                previous = ends[index] = change.Span.End;
                delta = checked(delta + change.NewText.Length - change.Span.Length); deltas[index] = delta;
            }
            _paths.Add(document.Path, (ends, deltas));
        }
    }
    internal int Position(string path, int position)
    {
        if (!_paths.TryGetValue(path, out var changes)) return position;
        var low = 0; var high = changes.Ends.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (changes.Ends[middle] <= position) low = middle + 1; else high = middle;
        }
        return checked(position + (low == 0 ? 0 : changes.Deltas[low - 1]));
    }
    internal TextSpan Span(string path, TextSpan span) => TextSpan.FromBounds(Position(path, span.Start), Position(path, span.End));
}
