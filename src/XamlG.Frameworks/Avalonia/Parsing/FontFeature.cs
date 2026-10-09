using System.Globalization;
using System.Text;

namespace XamlG.Frameworks.Avalonia.Parsing;

/// <summary>
/// Font feature
/// </summary>
internal record FontFeature
{
    private const int DefaultValue = 1;
    private const int InfinityEnd = -1;
    
    /// <summary>Gets or sets the tag.</summary>
    public string Tag
    {
        get;
        init;
    }

    /// <summary>Gets or sets the value.</summary>
    public int Value
    {
        get;
        init;
    }

    /// <summary>Gets or sets the start.</summary>
    public int Start
    {
        get;
        init;
    }

    /// <summary>Gets or sets the end.</summary>
    public int End
    {
        get;
        init;
    }
    
    /// <summary>
    /// Creates an instance of FontFeature.
    /// </summary>
    public FontFeature()
    {
        Tag = string.Empty;
        Value = DefaultValue;
        Start = 0;
        End = InfinityEnd;
    }

    /// <summary>
    /// Parses a string to return a <see cref="FontFeature"/>.
    /// Syntax is the following:
    ///  
    ///     Syntax 	        Value 	Start 	End 	 
    ///     Setting value: 	  	  	  	 
    ///     kern 	        1 	    0 	    ∞ 	    Turn feature on
    ///     +kern 	        1 	    0 	    ∞ 	    Turn feature on
    ///     -kern 	        0 	    0 	    ∞ 	    Turn feature off
    ///     kern=0 	        0 	    0 	    ∞ 	    Turn feature off
    ///     kern=1 	        1 	    0 	    ∞ 	    Turn feature on
    ///     aalt=2 	        2 	    0 	    ∞ 	    Choose 2nd alternate
    ///     Setting index: 	  	  	  	 
    ///     kern[] 	        1 	    0 	    ∞ 	    Turn feature on
    ///     kern[:] 	    1 	    0 	    ∞ 	    Turn feature on
    ///     kern[5:] 	    1 	    5 	    ∞ 	    Turn feature on, partial
    ///     kern[:5] 	    1 	    0 	    5 	    Turn feature on, partial
    ///     kern[3:5] 	    1 	    3 	    5 	    Turn feature on, range
    ///     kern[3] 	    1 	    3 	    3+1 	Turn feature on, single char
    ///     Mixing it all: 	  	  	  	 
    ///     aalt[3:5]=2 	2 	    3 	    5 	    Turn 2nd alternate on for range
    /// 
    /// </summary>
    /// <param name="s">The string.</param>
    /// <returns>The <see cref="FontFeature"/>.</returns>
    // ReSharper disable once UnusedMember.Global
    public static FontFeature Parse(string s)
    {
        if (s == null) throw new ArgumentNullException("input");
        return Parse(s.AsSpan());
    }

    public static FontFeature Parse(ReadOnlySpan<char> text)
    {
        var position = 0;
        White(text, ref position);
        var sign = position < text.Length && text[position] is '+' or '-' ? text[position++] : '\0';
        White(text, ref position);
        var tagStart = position;
        for (var i = 0; i < 4; i++)
            if (position == text.Length || !Word(text[position++])) return new FontFeature();
        White(text, ref position);
        ReadOnlySpan<char> startText = default, endText = default, valueText = default;
        var hasSeparator = false;
        if (position < text.Length && text[position] == '[')
        {
            position++;
            White(text, ref position);
            startText = Digits(text, ref position);
            var beforeWhite = position;
            White(text, ref position);
            if (position < text.Length && text[position] == ':')
            {
                hasSeparator = true;
                position++;
                White(text, ref position);
                endText = Digits(text, ref position);
            }
            else position = beforeWhite;
            White(text, ref position);
            if (position == text.Length || text[position++] != ']') return new FontFeature();
            White(text, ref position);
        }
        if (position < text.Length && text[position] == '=' && sign == '\0')
        {
            position++;
            White(text, ref position);
            valueText = Digits(text, ref position);
            if (valueText.IsEmpty)
            {
                var rest = text.Slice(position);
                if (rest.StartsWith("on".AsSpan(), StringComparison.Ordinal))
                { valueText = "1".AsSpan(); position += 2; }
                else if (rest.StartsWith("off".AsSpan(), StringComparison.Ordinal))
                { valueText = "0".AsSpan(); position += 3; }
                else return new FontFeature();
            }
        }
        White(text, ref position);
        if (position != text.Length) return new FontFeature();
        var hasStart = startText.TryParseInt(NumberStyles.None, CultureInfo.InvariantCulture, out var start);
        var hasEnd = endText.TryParseInt(NumberStyles.None, CultureInfo.InvariantCulture, out var end);
        var value = sign == '-' ? 0 : sign == '+' ? 1 :
            valueText.TryParseInt(NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : DefaultValue;
        return new FontFeature
        {
            Tag = text.Slice(tagStart, 4).ToString(),
            Start = hasStart ? start : 0,
            End = hasEnd ? end : hasStart && !hasSeparator ? unchecked(start + 1) : InfinityEnd,
            Value = value
        };
    }

    private static void White(ReadOnlySpan<char> text, ref int position)
    { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }

    private static ReadOnlySpan<char> Digits(ReadOnlySpan<char> text, scoped ref int position)
    {
        var start = position;
        while (position < text.Length && char.IsDigit(text[position])) position++;
        return text.Slice(start, position - start);
    }

    private static bool Word(char value) => char.GetUnicodeCategory(value) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;

    /// <summary>
    /// Gets a string representation of the <see cref="FontFeature"/>.
    /// </summary>
    /// <returns>The string representation.</returns>
    public override string ToString()
    {
        var result = new StringBuilder(128);
        
        if (Value == 0)
            result.Append('-');
        result.Append(Tag ?? string.Empty);

        if (Start != 0 || End != InfinityEnd)
        {
            result.Append('[');
            
            if (Start > 0)
                result.Append(Start.ToString(CultureInfo.InvariantCulture));
            
            if (End != Start + 1) 
            {
                result.Append(':');
                if (End != InfinityEnd)
                    result.Append(End.ToString(CultureInfo.InvariantCulture));
            }
            
            result.Append(']');
        }

        if (Value is DefaultValue or 0)
        {
            return result.ToString();
        }
        
        result.Append('=');
        result.Append(Value.ToString(CultureInfo.InvariantCulture));

        return result.ToString();
    }
}
