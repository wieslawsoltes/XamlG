using System.Globalization;
using System.Text;

namespace XamlG.Syntax;

/// <summary>Maps decoded XML UTF-16 ranges back to complete raw entities and line endings.
/// A position inside a surrogate-pair entity is not a legal edit boundary.</summary>
public sealed class XamlDecodedTextMap
{
    private readonly int[] _boundaries;
    private readonly int _sourceStart;
    private XamlDecodedTextMap(string text, int[] boundaries, int sourceStart)
    { Text = text; _boundaries = boundaries; _sourceStart = sourceStart; }
    public string Text { get; }
    public TextSpan ToSource(TextSpan decodedSpan)
    {
        if (decodedSpan.End > Text.Length || _boundaries[decodedSpan.Start] < 0 || _boundaries[decodedSpan.End] < 0)
            throw new ArgumentException("A source edit cannot split an XML entity or exceed the decoded value.", nameof(decodedSpan));
        return TextSpan.FromBounds(_sourceStart + _boundaries[decodedSpan.Start], _sourceStart + _boundaries[decodedSpan.End]);
    }
    public static XamlDecodedTextMap Create(string source, TextSpan span, bool attribute = true)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (span.End > source.Length) throw new ArgumentOutOfRangeException(nameof(span));
        var text = source.Substring(span.Start, span.Length);
        var output = new StringBuilder(text.Length);
        var boundaries = new List<int> { 0 };
        for (var i = 0; i < text.Length;)
        {
            var start = i; string value;
            if (text[i] == '&')
            {
                var end = text.IndexOf(';', i + 1);
                if (end < 0 || end - i > 32) throw new ArgumentException("Invalid XML entity in the source range.", nameof(source));
                var entity = text.Substring(i + 1, end - i - 1);
                value = entity switch { "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'", _ => Numeric(entity) };
                i = end + 1;
            }
            else if (text[i] == '\r')
            { i++; if (i < text.Length && text[i] == '\n') i++; value = attribute ? " " : "\n"; }
            else { var c = text[i++]; value = attribute && c is '\n' or '\t' ? " " : c.ToString(); }
            output.Append(value);
            for (var n = 1; n < value.Length; n++) boundaries.Add(i - start == value.Length ? start + n : -1);
            boundaries.Add(i);
        }
        return new(output.ToString(), boundaries.ToArray(), span.Start);
    }
    private static string Numeric(string entity)
    {
        var hex = entity.StartsWith("#x", StringComparison.Ordinal);
        if (!entity.StartsWith("#", StringComparison.Ordinal) || !int.TryParse(entity.Substring(hex ? 2 : 1),
            hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var scalar) ||
            !(scalar is 9 or 10 or 13 || scalar is >= 0x20 and <= 0xD7FF || scalar is >= 0xE000 and <= 0xFFFD || scalar is >= 0x10000 and <= 0x10FFFF))
            throw new ArgumentException("Invalid XML character entity.", nameof(entity));
        return char.ConvertFromUtf32(scalar);
    }
}
