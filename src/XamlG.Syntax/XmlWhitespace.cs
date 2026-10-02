using System.Text;
namespace XamlG.Syntax;
public static class XmlWhitespace
{
    public static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';
    public static string Normalize(string text, bool preserve) => preserve ? text : Collapse(text, true, true);
    public static string Collapse(string text, bool trimStart, bool trimEnd)
    {
        var output = new StringBuilder(text.Length); var pending = false;
        foreach (var c in text)
        {
            if (IsWhitespace(c)) { pending = true; continue; }
            if (pending && (output.Length > 0 || !trimStart)) output.Append(' ');
            pending = false; output.Append(c);
        }
        if (pending && !trimEnd && (output.Length > 0 || !trimStart)) output.Append(' ');
        return output.ToString();
    }
}
