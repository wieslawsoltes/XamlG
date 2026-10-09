using System;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal ref struct BracketSplitEnumerator
{
    private readonly ReadOnlySpan<char> _text;
    private readonly ReadOnlySpan<char> _separators;
    private readonly char _opening;
    private readonly char _closing;
    private readonly bool _removeEmpty;
    private readonly bool _trim;
    private int _position;

    public BracketSplitEnumerator(ReadOnlySpan<char> text, ReadOnlySpan<char> separators,
        char opening = '(', char closing = ')', StringSplitOptions options = StringSplitOptions.None)
    {
        if (opening == closing)
            throw new ArgumentException($"Opening bracket and closing bracket cannot be the same character '{opening}'.", "closingBracket");
        _text = text;
        _separators = separators;
        _opening = opening;
        _closing = closing;
        _removeEmpty = (options & StringSplitOptions.RemoveEmptyEntries) != 0;
        _trim = (options & (StringSplitOptions)2) != 0;
        _position = 0;
        Current = default;
        Count = 0;
        // Validate the entire bracket structure before parsing any element. This
        // retains the original splitter's error ordering and permits exact sizing.
        while (MoveNext()) Count++;
        _position = 0;
        Current = default;
    }

    public int Count { get; private set; }
    public ReadOnlySpan<char> Current { get; private set; }
    public BracketSplitEnumerator GetEnumerator() => this;

    public bool MoveNext()
    {
        while (_position <= _text.Length)
        {
            var start = _position;
            var end = start;
            var depth = 0;
            for (; end < _text.Length; end++)
            {
                var c = _text[end];
                if (c == _opening) depth++;
                else if (c == _closing)
                {
                    if (depth == 0) throw new FormatException($"Unmatched closing bracket '{_closing}' at position {end}.");
                    depth--;
                }
                else if (depth == 0 && _separators.IndexOf(c) >= 0) break;
            }
            if (depth != 0) throw new FormatException($"Unmatched opening bracket '{_opening}' in input string.");
            _position = end + 1;
            Current = _text.Slice(start, end - start);
            if (_trim) Current = Current.Trim();
            if (!_removeEmpty || !Current.IsEmpty) return true;
        }
        return false;
    }
}
