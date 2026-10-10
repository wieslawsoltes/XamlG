namespace XamlG.Syntax;

/// <summary>Allocation-free lexical primitives shared by the XML, markup and type-name grammars.
/// XML whitespace deliberately remains distinct from the CLR whitespace used by XAML literals.</summary>
internal static class XamlTextScanner
{
    public static int SkipWhitespace(string text, int position, int end)
    {
        while (position < end && char.IsWhiteSpace(text[position])) position++;
        return position;
    }

    public static int SkipXmlWhitespace(string text, int position)
    {
        while (position < text.Length && XmlWhitespace.IsWhitespace(text[position])) position++;
        return position;
    }

    public static int TextEnd(string text, int position)
    {
        var relative = text.AsSpan(position).IndexOf('<');
        return relative < 0 ? text.Length : position + relative;
    }

    public static int AttributeValueEnd(string text, int position, char quote)
    {
        var relative = text.AsSpan(position).IndexOfAny(quote, '<');
        return relative < 0 ? text.Length : position + relative;
    }

    public static int XmlNameEnd(string text, int position)
    {
        // Wide elements already maintain their own duplicate-name set. Do not
        // hash their usually unique attributes a second time for atomization.
        while (position < text.Length && !IsXmlNameDelimiter(text[position])) position++;
        return position;
    }

    public static int XmlNameEnd(string text, int position, out int hash)
    {
        // Compute the lookup hash during the existing name scan, not in a second pass.
        var value = 2166136261U;
        while (position < text.Length)
        {
            var c = text[position];
            if (IsXmlNameDelimiter(c)) break;
            value = unchecked((value ^ c) * 16777619U);
            position++;
        }
        hash = unchecked((int)value);
        return position;
    }

    private static bool IsXmlNameDelimiter(char value) => XmlWhitespace.IsWhitespace(value) ||
        value is '<' or '>' or '/' or '=' or '\'' or '"' or '?' or '!';
}
