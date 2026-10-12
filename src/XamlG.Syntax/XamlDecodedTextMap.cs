using System.Text;

namespace XamlG.Syntax;

/// <summary>Maps decoded XML UTF-16 ranges back to complete raw entities and line endings.
/// A position inside a surrogate-pair entity is not a legal edit boundary.</summary>
public sealed class XamlDecodedTextMap
{
    // Small/dense inputs use direct integer boundaries. Sparse inputs store only
    // offset changes and indivisible-entity boundaries; null means identity offsets.
    // Both array representations are privately owned and immutable after publication.
    private readonly Array? _boundaries;
    private readonly int _sourceStart;
    private readonly struct Boundary(int position, int offset)
    {
        public int Position { get; } = position;
        public int Offset { get; } = offset;
    }
    private XamlDecodedTextMap(string text, Array? boundaries, int sourceStart)
    { Text = text; _boundaries = boundaries; _sourceStart = sourceStart; }
    public string Text { get; }
    public TextSpan ToSource(TextSpan decodedSpan)
    {
        if (decodedSpan.End > Text.Length) throw InvalidBoundary();
        if (_boundaries == null) return new(_sourceStart + decodedSpan.Start, decodedSpan.Length);
        var start = MapBoundary(decodedSpan.Start);
        var end = decodedSpan.Length == 0 ? start : MapBoundary(decodedSpan.End);
        if (start < 0 || end < 0) throw InvalidBoundary();
        return TextSpan.FromBounds(_sourceStart + start, _sourceStart + end);
    }
    private static ArgumentException InvalidBoundary() => new(
        "A source edit cannot split an XML entity or exceed the decoded value.", "decodedSpan");

    private int MapBoundary(int position)
    {
        if (_boundaries is int[] dense) return dense[position];
        var changes = (Boundary[])_boundaries!;
        // Upper bound: all integer positions between checkpoints have the same
        // nonnegative offset. A forbidden checkpoint is immediately followed by
        // its entity end, so it can be selected only for that exact position.
        var low = 0; var high = changes.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (changes[middle].Position <= position) low = middle + 1;
            else high = middle;
        }
        if (low == 0) return position;
        var offset = changes[low - 1].Offset;
        return offset < 0 ? -1 : position + offset;
    }

    public static XamlDecodedTextMap Create(string source, TextSpan span, bool attribute = true)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (span.End > source.Length) throw new ArgumentOutOfRangeException(nameof(span));
        var text = source.AsSpan(span.Start, span.Length);
        var firstEscape = XmlEntityDecoder.FindEscape(text, attribute);
        if (firstEscape < 0) return new(text.ToString(), null, span.Start);
        // Short markup values keep their original constant-time boundary lookup.
        if (text.Length <= 128) return CreateDense(text, span.Start, attribute);
        var output = new StringBuilder(text.Length);
        output.Append(source, span.Start, firstEscape);
        List<Boundary>? changes = null;
        var i = firstEscape;
        while (i < text.Length)
        {
            if (text[i] == '&')
            {
                var relativeEnd = XmlEntityDecoder.FindEntityEnd(text.Slice(i + 1));
                if (relativeEnd < 0) throw new ArgumentException("Invalid XML entity in the source range.", nameof(source));
                var entity = text.Slice(i + 1, relativeEnd);
                if (!XmlEntityDecoder.TryDecodeEntity(entity, out var scalar))
                    throw new ArgumentException("Invalid XML character entity.", nameof(entity));
                i += relativeEnd + 2;
                changes ??= new();
                if (scalar <= 0xFFFF) output.Append((char)scalar);
                else
                {
                    changes.Add(new(output.Length + 1, -1));
                    scalar -= 0x10000;
                    output.Append((char)(0xD800 + (scalar >> 10)));
                    output.Append((char)(0xDC00 + (scalar & 0x3FF)));
                }
                changes.Add(new(output.Length, i - output.Length));
            }
            else if (text[i] == '\r')
            {
                i++;
                var pair = i < text.Length && text[i] == '\n';
                if (pair) i++;
                output.Append(attribute ? ' ' : '\n');
                if (pair) (changes ??= new()).Add(new(output.Length, i - output.Length));
            }
            else { output.Append(' '); i++; } // Attribute-only LF/TAB escapes.
            // Ordinary UTF-16, including literal surrogate halves, maps affinely.
            // Copy each plain run once without materializing one entry per character.
            var nextEscape = XmlEntityDecoder.FindEscape(text.Slice(i), attribute);
            var count = nextEscape < 0 ? text.Length - i : nextEscape;
            output.Append(source, span.Start + i, count);
            i += count;
        }
        return new(output.ToString(), Finish(changes, output.Length), span.Start);
    }

    private static Array? Finish(List<Boundary>? changes, int length)
    {
        if (changes == null) return null;
        if (2L * changes.Count < (long)length + 1) return changes.ToArray();
        // Dense late changes can defeat any fixed prefix-density heuristic. Select
        // by actual payload size instead. Temporary checkpoints die here; the
        // retained map never pays more payload bytes than direct boundaries.
        var dense = new int[length + 1];
        var next = 0; var offset = 0;
        for (var position = 0; position <= length; position++)
        {
            if (next < changes.Count && changes[next].Position == position)
            {
                var change = changes[next++];
                if (change.Offset < 0) { dense[position] = -1; continue; }
                offset = change.Offset;
            }
            dense[position] = position + offset;
        }
        return dense;
    }

    private static XamlDecodedTextMap CreateDense(ReadOnlySpan<char> text, int sourceStart, bool attribute)
    {
        // Original decoder/boundary representation for small values.
        var output = new StringBuilder(text.Length);
        var boundaries = new List<int> { 0 };
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '&')
            {
                var relativeEnd = XmlEntityDecoder.FindEntityEnd(text.Slice(i + 1));
                if (relativeEnd < 0) throw new ArgumentException("Invalid XML entity in the source range.", "source");
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
        return new(output.ToString(), boundaries.ToArray(), sourceStart);
    }
}
