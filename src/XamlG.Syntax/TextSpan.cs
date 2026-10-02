namespace XamlG.Syntax;

/// <summary>A half-open UTF-16 source interval.</summary>
public readonly record struct TextSpan
{
    public TextSpan(int start, int length)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (length < 0 || start > int.MaxValue - length) throw new ArgumentOutOfRangeException(nameof(length));
        Start = start; Length = length;
    }
    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;
    public bool Contains(int position) => position >= Start && position < End;
    public bool Contains(TextSpan other) => other.Start >= Start && other.End <= End;
    public bool OverlapsWith(TextSpan other) => Start < other.End && other.Start < End;
    public static TextSpan FromBounds(int start, int end) => new(start, checked(end - start));
    public override string ToString() => $"[{Start}..{End})";
}
