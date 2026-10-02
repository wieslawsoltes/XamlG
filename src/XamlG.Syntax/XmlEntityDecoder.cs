using System.Globalization;
using System.Text;
namespace XamlG.Syntax;

internal static class XmlEntityDecoder
{
    public static string Decode(string text, int sourceStart, Action<XamlDiagnostic> report, bool attribute)
    {
        var output = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; output.Append(attribute ? ' ' : '\n'); continue; }
            if (attribute && (c == '\n' || c == '\t')) { output.Append(' '); continue; }
            if (c != '&') { output.Append(c); continue; }
            var end = text.IndexOf(';', i + 1);
            if (end < 0 || end - i > 32) { report(new("XG0008", "Unterminated XML entity.", new(sourceStart + i, 1))); output.Append(c); continue; }
            var name = text.Substring(i + 1, end - i - 1);
            string? value = name switch { "lt" => "<", "gt" => ">", "amp" => "&", "apos" => "'", "quot" => "\"", _ => null };
            if (value == null && name.StartsWith("#", StringComparison.Ordinal))
            {
                var hex = name.StartsWith("#x", StringComparison.Ordinal);
                if (int.TryParse(name.Substring(hex ? 2 : 1), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var scalar) &&
                    (scalar is 9 or 10 or 13 || scalar >= 0x20 && scalar <= 0xD7FF || scalar >= 0xE000 && scalar <= 0xFFFD || scalar >= 0x10000 && scalar <= 0x10FFFF))
                    value = char.ConvertFromUtf32(scalar);
            }
            if (value == null)
            {
                report(new("XG0008", $"Unknown or invalid XML entity '&{name};'.", new(sourceStart + i, end - i + 1)));
                output.Append(text, i, end - i + 1);
            }
            else output.Append(value);
            i = end;
        }
        return output.ToString();
    }
}
