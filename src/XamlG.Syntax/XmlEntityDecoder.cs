using System.Buffers;
using System.Globalization;
using XamlG.Internal;

namespace XamlG.Syntax;

internal static class XmlEntityDecoder
{
    private static readonly char[] AttributeEscapes = { '&', '\r', '\n', '\t' };
    private static readonly char[] TextEscapes = { '&', '\r' };

    internal static int FindEscape(ReadOnlySpan<char> text, bool attribute) =>
        text.IndexOfAny(attribute ? AttributeEscapes : TextEscapes);

    public static string Decode(ReadOnlySpan<char> text, int sourceStart, Action<XamlDiagnostic> report, bool attribute)
    {
        var firstEscape = FindEscape(text, attribute);
        if (firstEscape < 0) return text.ToString();
        char[]? rented = null;
        Span<char> output = text.Length <= 256 ? stackalloc char[text.Length] : (rented = ArrayPool<char>.Shared.Rent(text.Length));
        try
        {
            text.Slice(0, firstEscape).CopyTo(output);
            var written = firstEscape;
            for (var i = firstEscape; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    output[written++] = attribute ? ' ' : '\n';
                    continue;
                }
                if (attribute && (c == '\n' || c == '\t')) { output[written++] = ' '; continue; }
                if (c != '&') { output[written++] = c; continue; }
                var relativeEnd = text.Slice(i + 1).IndexOf(';');
                if (relativeEnd < 0 || relativeEnd + 1 > 32)
                {
                    report(new("XG0008", "Unterminated XML entity.", new(sourceStart + i, 1)));
                    output[written++] = c;
                    continue;
                }
                var end = i + 1 + relativeEnd;
                var name = text.Slice(i + 1, relativeEnd);
                if (!TryDecodeEntity(name, out var scalar))
                {
                    report(new("XG0008", $"Unknown or invalid XML entity '&{name.ToString()};'.", new(sourceStart + i, end - i + 1)));
                    var raw = text.Slice(i, end - i + 1);
                    raw.CopyTo(output.Slice(written));
                    written += raw.Length;
                }
                else if (scalar <= 0xFFFF) output[written++] = (char)scalar;
                else
                {
                    scalar -= 0x10000;
                    output[written++] = (char)(0xD800 + (scalar >> 10));
                    output[written++] = (char)(0xDC00 + (scalar & 0x3FF));
                }
                i = end;
            }
            return output.Slice(0, written).ToString();
        }
        finally { if (rented != null) ArrayPool<char>.Shared.Return(rented); }
    }

    internal static bool TryDecodeEntity(ReadOnlySpan<char> name, out int scalar)
    {
        if (name.SequenceEqual("lt".AsSpan())) scalar = '<';
        else if (name.SequenceEqual("gt".AsSpan())) scalar = '>';
        else if (name.SequenceEqual("amp".AsSpan())) scalar = '&';
        else if (name.SequenceEqual("apos".AsSpan())) scalar = '\'';
        else if (name.SequenceEqual("quot".AsSpan())) scalar = '"';
        else
        {
            scalar = 0;
            if (name.IsEmpty || name[0] != '#') return false;
            var hex = name.Length > 1 && name[1] == 'x';
            return SpanNumberParser.TryParseInt(name.Slice(hex ? 2 : 1),
                hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out scalar) &&
                (scalar is 9 or 10 or 13 || scalar is >= 0x20 and <= 0xD7FF ||
                    scalar is >= 0xE000 and <= 0xFFFD || scalar is >= 0x10000 and <= 0x10FFFF);
        }
        return true;
    }
}
