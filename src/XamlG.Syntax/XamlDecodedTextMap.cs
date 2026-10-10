using System.Text;

namespace XamlG.Syntax;

/// <summary>Maps decoded XML UTF-16 ranges back to complete raw entities and line endings.
/// A position inside a surrogate-pair entity is not a legal edit boundary.</summary>
public sealed class XamlDecodedTextMap
{
    private readonly int[]? _boundaries;
    private readonly int _sourceStart;
    private XamlDecodedTextMap(string text, int[]? boundaries, int sourceStart)
    { Text = text; _boundaries = boundaries; _sourceStart = sourceStart; }
    public string Text { get; }
    public TextSpan ToSource(TextSpan decodedSpan)
    {
        if (decodedSpan.End > Text.Length || _boundaries != null &&
            (_boundaries[decodedSpan.Start] < 0 || _boundaries[decodedSpan.End] < 0))
            throw new ArgumentException("A source edit cannot split an XML entity or exceed the decoded value.", nameof(decodedSpan));
        if (_boundaries == null) return new(_sourceStart + decodedSpan.Start, decodedSpan.Length);
        return TextSpan.FromBounds(_sourceStart + _boundaries[decodedSpan.Start], _sourceStart + _boundaries[decodedSpan.End]);
    }
    public static XamlDecodedTextMap Create(string source, TextSpan span, bool attribute = true)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (span.End > source.Length) throw new ArgumentOutOfRangeException(nameof(span));
        var text = source.AsSpan(span.Start, span.Length);
        if (XmlEntityDecoder.FindEscape(text, attribute) < 0)
            return new(text.ToString(), null, span.Start);
        var output = new StringBuilder(text.Length);
        var boundaries = new List<int> { 0 };
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '&')
            {
                var relativeEnd = XmlEntityDecoder.FindEntityEnd(text.Slice(i + 1));
                if (relativeEnd < 0) throw new ArgumentException("Invalid XML entity in the source range.", nameof(source));
                var entity = text.Slice(i + 1, relativeEnd);
                if (!XmlEntityDecoder.TryDecodeEntity(entity, out var scalar))
                    throw new ArgumentException("Invalid XML character entity.", nameof(entity));
                i += relativeEnd + 2;
                if (scalar <= 0xFFFF) output.Append((char)scalar);
                else
                {
                    scalar -= 0x10000;
                    output.Append((char)(0xD800 + (scalar >> 10)));
                    output.Append((char)(0xDC00 + (scalar & 0x3FF)));
                    boundaries.Add(-1);
                }
            }
            else if (text[i] == '\r')
            { i++; if (i < text.Length && text[i] == '\n') i++; output.Append(attribute ? ' ' : '\n'); }
            else
            {
                var c = text[i++];
                output.Append(attribute && c is '\n' or '\t' ? ' ' : c);
            }
            boundaries.Add(i);
        }
        return new(output.ToString(), boundaries.ToArray(), span.Start);
    }
}
