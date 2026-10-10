using System.Buffers;
using System.Collections.Immutable;
namespace XamlG.Syntax;

/// <summary>Balanced markup-extension parser; braces inside quoted strings never affect nesting.</summary>
public static class MarkupExtensionParser
{
    /// <summary>Parses decoded XML text while retaining exact raw-source ranges, including entities.</summary>
    public static MarkupExtensionSyntax? ParseAtSource(string text, TextSpan span, string source, Action<XamlDiagnostic> report)
    {
        if (text.Length == 0 || text[0] != '{' || text.StartsWith("{}", StringComparison.Ordinal)) return null;
        // The overwhelmingly common identity mapping needs no map object, copied
        // source string, remapping delegate, second argument array or record copies.
        // If identical raw text would normalize/decode differently, the old path
        // also falls back to Parse, so this preserves that public API behavior.
        if (source != null && span.Length == text.Length && span.End <= source.Length &&
            string.CompareOrdinal(source, span.Start, text, 0, text.Length) == 0)
            return Parse(text, span, report);
        return ParseMapped(text, span, source, report);
    }
    private static MarkupExtensionSyntax? ParseMapped(string text, TextSpan span, string source, Action<XamlDiagnostic> report)
    {
        XamlDecodedTextMap map;
        try { map = XamlDecodedTextMap.Create(source, span); }
        catch (ArgumentException) { return Parse(text, span, report); }
        if (map.Text != text) return Parse(text, span, report);
        var parsed = Parse(text, new(0, text.Length), diagnostic => report(diagnostic with { Span = map.ToSource(diagnostic.Span) }));
        return parsed == null ? null : parsed with
        {
            Span = span,
            NameSpan = parsed.NameSpan is { } name ? map.ToSource(name) : null,
            Arguments = parsed.Arguments.Select(argument => argument with
            {
                Span = map.ToSource(argument.Span),
                NameSpan = argument.NameSpan is { } argumentName ? map.ToSource(argumentName) : null,
                ValueSpan = argument.ValueSpan is { } value ? map.ToSource(value) : null
            }).ToImmutableArray()
        };
    }
    public static MarkupExtensionSyntax? Parse(string text, TextSpan span, Action<XamlDiagnostic> report)
    {
        if (text.Length == 0 || text[0] != '{' || text.StartsWith("{}", StringComparison.Ordinal)) return null;
        var end = text.Length;
        if (text[end - 1] != '}') { report(new("XG0010", "Markup extension is missing a closing brace.", span)); } else end--;
        var position = XamlTextScanner.SkipWhitespace(text, 1, end);
        var nameStart = position;
        while (position < end && !char.IsWhiteSpace(text[position]) && text[position] != ',') position++;
        var name = text.Substring(nameStart, position - nameStart);
        if (name.Length == 0) { report(new("XG0010", "Markup extension requires a type name.", span)); return null; }
        position = XamlTextScanner.SkipWhitespace(text, position, end);
        if (position < end && text[position] == ',') position++;
        var arguments = ImmutableArray.CreateBuilder<MarkupArgumentSyntax>(); HashSet<string>? names = null; var sawNamed = false;
        while (position < end)
        {
            position = XamlTextScanner.SkipWhitespace(text, position, end);
            var start = position; var equals = -1; var depth = 0; char quote = '\0'; var escaped = false;
            while (position < end)
            {
                var c = text[position];
                if (escaped) { escaped = false; position++; continue; }
                if (c == '\\' && quote != '\0') { escaped = true; position++; continue; }
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                        position++;
                        if (depth == 0 && equals >= 0 && NextIsNamedArgument(text, position, end)) break;
                    }
                    else position++;
                    continue;
                }
                if (c is '\'' or '"') quote = c;
                else if (c == '{' || c == '(' || c == '[') depth++;
                else if (c == '}' || c == ')' || c == ']') depth--;
                else if (depth == 0 && c == '=' && equals < 0) equals = position;
                else if (depth == 0 && c == ',') break;
                position++;
            }
            if (quote != '\0' || depth != 0) report(new("XG0010", "Unbalanced markup-extension argument.", new(span.Start + start, position - start)));
            var argumentSpan = new TextSpan(span.Start + start, position - start);
            string? key = equals < 0 ? null : text.AsSpan(start, equals - start).Trim().ToString();
            var valueStart = XamlTextScanner.SkipWhitespace(text, equals < 0 ? start : equals + 1, position);
            var valueEnd = position;
            while (valueEnd > valueStart && char.IsWhiteSpace(text[valueEnd - 1])) valueEnd--;
            var valueText = text.AsSpan(valueStart, valueEnd - valueStart);
            string value;
            if (valueText.Length >= 2 && (valueText[0] is '\'' or '"') && valueText[valueText.Length - 1] == valueText[0])
            { value = Unquote(valueText.Slice(1, valueText.Length - 2)); valueStart++; valueEnd--; }
            else value = valueText.ToString();
            if (key != null) { sawNamed = true; if (key.Length == 0 || !(names ??= new(StringComparer.Ordinal)).Add(key)) report(new("XG0010", "A named markup argument is empty or duplicated.", argumentSpan)); }
            else if (sawNamed) report(new("XG0010", "Positional arguments must precede named arguments.", argumentSpan));
            if (value.Length != 0 || key != null) arguments.Add(new(key, value, argumentSpan)
            {
                NameSpan = key == null ? null : new(span.Start + start, key.Length),
                ValueSpan = TextSpan.FromBounds(span.Start + valueStart, span.Start + valueEnd)
            });
            if (position < end && text[position] == ',') position++;
        }
        return new(name, arguments.ToImmutable(), span) { NameSpan = new(span.Start + nameStart, name.Length) };
    }
    private static bool NextIsNamedArgument(string text, int position, int end)
    {
        var start = position;
        position = XamlTextScanner.SkipWhitespace(text, position, end);
        if (position == start) return false;
        var nameStart = position;
        while (position < end && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or ':' or '.' or '-')) position++;
        if (position == nameStart) return false;
        position = XamlTextScanner.SkipWhitespace(text, position, end);
        return position < end && text[position] == '=';
    }
    private static string Unquote(ReadOnlySpan<char> value)
    {
        var escape = value.IndexOf('\\');
        if (escape < 0) return value.ToString();
        char[]? rented = null;
        Span<char> result = value.Length <= 256 ? stackalloc char[value.Length] : (rented = ArrayPool<char>.Shared.Rent(value.Length));
        try
        {
            value.Slice(0, escape).CopyTo(result);
            var length = escape;
            for (var i = escape; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length) i++;
                result[length++] = value[i];
            }
            return result.Slice(0, length).ToString();
        }
        finally { if (rented != null) ArrayPool<char>.Shared.Return(rented); }
    }
}
