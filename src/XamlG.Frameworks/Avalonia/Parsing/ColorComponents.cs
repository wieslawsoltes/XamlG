using System;

namespace XamlG.Frameworks.Avalonia.Parsing;

// CSS colors have exactly three or four comma-separated components. Record
// their boundaries without creating the array and strings from string.Split.
internal readonly ref struct ColorComponents
{
    private readonly ReadOnlySpan<char> _text;
    private readonly int _first;
    private readonly int _second;
    private readonly int _third;

    public ColorComponents(ReadOnlySpan<char> text)
    {
        _text = text;
        _first = text.IndexOf(',');
        _second = _first < 0 ? -1 : NextComma(text, _first);
        _third = _second < 0 ? -1 : NextComma(text, _second);
        Length = _second < 0 ? 0 : _third < 0 ? 3 : NextComma(text, _third) < 0 ? 4 : 0;
    }

    public int Length { get; }
    public ReadOnlySpan<char> this[int index] => index switch
    {
        0 => _text.Slice(0, _first),
        1 => _text.Slice(_first + 1, _second - _first - 1),
        2 => _text.Slice(_second + 1, (_third < 0 ? _text.Length : _third) - _second - 1),
        3 => _text.Slice(_third + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static int NextComma(ReadOnlySpan<char> text, int previous)
    {
        var next = text.Slice(previous + 1).IndexOf(',');
        return next < 0 ? -1 : previous + 1 + next;
    }
}
