using System.Text;
namespace XamlG.Syntax;
public static class XmlWhitespace
{
    public static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';
    public static string Normalize(string text, bool preserve) => preserve ? text : Collapse(text, true, true);
    public static string Collapse(string text, bool trimStart, bool trimEnd)
    {
        // Delay output allocation until an actual change. Canonical text (including
        // allowed single boundary spaces) returns its existing immutable string.
        for (var position = 0; position < text.Length;)
        {
            if (!IsWhitespace(text[position])) { position++; continue; }
            var start = position++;
            while (position < text.Length && IsWhitespace(text[position])) position++;
            if (start == 0 && position == text.Length)
                return !trimStart && !trimEnd ? " " : string.Empty;
            if (position - start == 1 && text[start] == ' ' &&
                (start != 0 || !trimStart) && (position != text.Length || !trimEnd)) continue;
            return CollapseChanged(text, start, trimStart, trimEnd);
        }
        return text;
    }

    private static string CollapseChanged(string text, int position, bool trimStart, bool trimEnd)
    {
        var output = new StringBuilder(text.Length);
        output.Append(text, 0, position);
        while (position < text.Length)
        {
            var whitespace = position;
            while (position < text.Length && IsWhitespace(text[position])) position++;
            if (position != whitespace && (output.Length != 0 || !trimStart) &&
                (position != text.Length || !trimEnd)) output.Append(' ');
            var content = position;
            while (position < text.Length && !IsWhitespace(text[position])) position++;
            // Copy unchanged runs rather than dispatching one Append per code unit.
            output.Append(text, content, position - content);
        }
        return output.ToString();
    }
}
