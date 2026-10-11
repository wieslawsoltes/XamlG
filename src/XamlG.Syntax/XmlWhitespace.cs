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
            return CollapseChanged(text, start, position, trimStart, trimEnd);
        }
        return text;
    }

    private static string CollapseChanged(string text, int prefixLength, int position, bool trimStart, bool trimEnd)
    {
        var output = new StringBuilder(text.Length);
        output.Append(text, 0, prefixLength);
        // Collapse already scanned the first changed whitespace run. Begin after
        // it with pending=true instead of reading those characters a second time.
        // A single pending-space loop avoids substring-Append dispatch for every
        // tiny content run in whitespace-dense values, while preserving the
        // unchanged prefix and the original XML-only trim-state machine.
        var pending = true;
        for (; position < text.Length; position++)
        {
            var character = text[position];
            if (IsWhitespace(character)) { pending = true; continue; }
            if (pending && (output.Length != 0 || !trimStart)) output.Append(' ');
            pending = false;
            output.Append(character);
        }
        if (pending && !trimEnd && (output.Length != 0 || !trimStart)) output.Append(' ');
        return output.ToString();
    }
}
