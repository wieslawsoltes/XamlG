using System.Collections.Immutable;
namespace XamlG.Syntax;
public sealed class SourceLineMap
{
    private readonly ImmutableArray<int> _starts;
    private readonly int _length;
    public SourceLineMap(string text)
    {
        _length = text.Length; var starts = ImmutableArray.CreateBuilder<int>(); starts.Add(0);
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; starts.Add(i + 1); }
            else if (text[i] == '\n') starts.Add(i + 1);
        _starts = starts.ToImmutable();
    }
    public int LineCount => _starts.Length;
    public SourceLinePosition GetPosition(int offset)
    {
        if (offset < 0 || offset > _length) throw new ArgumentOutOfRangeException(nameof(offset));
        var low = 0; var high = _starts.Length - 1;
        while (low <= high) { var middle = low + (high - low) / 2; if (_starts[middle] <= offset) low = middle + 1; else high = middle - 1; }
        return new(high, offset - _starts[high]);
    }
    public int GetOffset(SourceLinePosition position)
    {
        if (position.Line < 0 || position.Line >= _starts.Length || position.Character < 0) throw new ArgumentOutOfRangeException(nameof(position));
        var end = position.Line + 1 < _starts.Length ? _starts[position.Line + 1] : _length;
        var offset = checked(_starts[position.Line] + position.Character);
        if (offset > end) throw new ArgumentOutOfRangeException(nameof(position));
        return offset;
    }
}
