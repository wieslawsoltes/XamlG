using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Styling;

internal sealed record AvaloniaPropertyName(string? Prefix, string? Owner, string Name)
{
    public static AvaloniaPropertyName? Parse(string text)
    {
        if (text.Length == 0) return null;
        var position = 0;
        var parenthesized = text[position] == '(';
        if (parenthesized) position++;
        var closed = false;
        string? prefix = null, owner = null, name = null;
        while (position < text.Length)
        {
            var start = position;
            if (IsStart(text[position]))
                while (position < text.Length && IsPart(text[position])) position++;
            if (position == start)
            {
                if (parenthesized && text[position] == ')') { position++; closed = true; break; }
                return null;
            }
            var token = text.Substring(start, position - start);
            if (position < text.Length && text[position] == ':')
            {
                if (prefix != null) return null;
                prefix = token; position++;
            }
            else if (position < text.Length && text[position] == '.')
            {
                if (owner != null) return null;
                owner = token; position++;
            }
            else name = token;
        }
        return name == null || position != text.Length || parenthesized && (owner == null || !closed)
            ? null : new(prefix, owner, name);
    }

    private static bool IsStart(char value) => char.IsLetter(value) || value == '_';
    private static bool IsPart(char value) => IsStart(value) || CharUnicodeInfo.GetUnicodeCategory(value) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation or
        UnicodeCategory.Format or UnicodeCategory.DecimalDigitNumber;
}
