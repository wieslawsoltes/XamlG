namespace XamlG.Syntax;

/// <summary>Indexes the smallest containing element at each source interval. Closed
/// end positions and equal-length ties retain the original preorder lookup rules.</summary>
internal sealed class XamlElementIndex
{
    private readonly Entry[] _entries;

    public XamlElementIndex(XamlElementSyntax root)
    {
        var elements = root.DescendantsAndSelf().ToArray();
        var events = new Event[elements.Length * 2];
        for (var i = 0; i < elements.Length; i++)
        {
            events[i * 2] = new(elements[i].Span.Start, i, true);
            // The old editor lookup includes the end cursor position. Use long
            // for end + 1 so even the largest representable span cannot wrap.
            events[i * 2 + 1] = new((long)elements[i].Span.End + 1, i, false);
        }
        Array.Sort(events, static (left, right) => left.Position.CompareTo(right.Position));
        var active = new SortedSet<int>(Comparer<int>.Create((left, right) =>
        {
            var length = elements[left].Span.Length.CompareTo(elements[right].Span.Length);
            return length != 0 ? length : left.CompareTo(right);
        }));
        var entries = new List<Entry>();
        XamlElementSyntax? previous = null;
        for (var i = 0; i < events.Length;)
        {
            var position = events[i].Position;
            do
            {
                var change = events[i++];
                if (change.Enter) active.Add(change.Element);
                else active.Remove(change.Element);
            } while (i < events.Length && events[i].Position == position);
            var current = active.Count == 0 ? null : elements[active.Min];
            if (ReferenceEquals(current, previous)) continue;
            entries.Add(new(position, current));
            previous = current;
        }
        _entries = entries.ToArray();
    }

    public XamlElementSyntax? Find(int position)
    {
        var low = 0;
        var high = _entries.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_entries[middle].Position <= position) low = middle + 1;
            else high = middle;
        }
        return low == 0 ? null : _entries[low - 1].Element;
    }

    private readonly struct Entry(long position, XamlElementSyntax? element)
    {
        public long Position { get; } = position;
        public XamlElementSyntax? Element { get; } = element;
    }

    private readonly struct Event(long position, int element, bool enter)
    {
        public long Position { get; } = position;
        public int Element { get; } = element;
        public bool Enter { get; } = enter;
    }
}
