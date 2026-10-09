using System;

namespace XamlG.Internal;

// The span split enumerator in newer BCLs is not part of netstandard2.0.
// Keep tokens as slices until a caller actually needs an owned string.
internal ref struct SpanSplitEnumerator
{
    private ReadOnlySpan<char> _remaining;
    private readonly ReadOnlySpan<char> _separators;
    private readonly char _separator;
    private readonly bool _multiple;
    private readonly bool _removeEmpty;
    private readonly bool _trim;
    private bool _finished;

    public SpanSplitEnumerator(ReadOnlySpan<char> text, char separator, bool removeEmpty = false, bool trim = false)
    {
        _remaining = text;
        _separators = default;
        _separator = separator;
        _multiple = false;
        _removeEmpty = removeEmpty;
        _trim = trim;
        _finished = false;
        Current = default;
    }

    public SpanSplitEnumerator(ReadOnlySpan<char> text, ReadOnlySpan<char> separators, bool removeEmpty = false, bool trim = false)
    {
        _remaining = text;
        _separators = separators;
        _separator = default;
        _multiple = true;
        _removeEmpty = removeEmpty;
        _trim = trim;
        _finished = false;
        Current = default;
    }

    public ReadOnlySpan<char> Current { get; private set; }
    public SpanSplitEnumerator GetEnumerator() => this;

    public bool MoveNext()
    {
        while (!_finished)
        {
            var index = _multiple ? _remaining.IndexOfAny(_separators) : _remaining.IndexOf(_separator);
            if (index < 0)
            {
                Current = _remaining;
                _remaining = default;
                _finished = true;
            }
            else
            {
                Current = _remaining.Slice(0, index);
                _remaining = _remaining.Slice(index + 1);
            }
            if (_trim) Current = Current.Trim();
            if (!_removeEmpty || !Current.IsEmpty) return true;
        }
        return false;
    }
}
