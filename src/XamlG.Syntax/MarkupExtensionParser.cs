using System.Collections.Immutable;
namespace XamlG.Syntax;

/// <summary>Balanced markup-extension parser; braces inside quoted strings never affect nesting.</summary>
public static class MarkupExtensionParser
{
    public static MarkupExtensionSyntax? Parse(string text, TextSpan span, Action<XamlDiagnostic> report)
    {
        if (text.Length == 0 || text[0] != '{' || text.StartsWith("{}", StringComparison.Ordinal)) return null;
        var end = text.Length;
        if (text[end - 1] != '}') { report(new("XG0010", "Markup extension is missing a closing brace.", span)); } else end--;
        var position = 1; while (position < end && char.IsWhiteSpace(text[position])) position++;
        var nameStart = position;
        while (position < end && !char.IsWhiteSpace(text[position]) && text[position] != ',') position++;
        var name = text.Substring(nameStart, position - nameStart);
        if (name.Length == 0) { report(new("XG0010", "Markup extension requires a type name.", span)); return null; }
        while (position < end && char.IsWhiteSpace(text[position])) position++;
        if (position < end && text[position] == ',') position++;
        var arguments = ImmutableArray.CreateBuilder<MarkupArgumentSyntax>(); var names = new HashSet<string>(StringComparer.Ordinal); var sawNamed = false;
        while (position < end)
        {
            while (position < end && char.IsWhiteSpace(text[position])) position++;
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
            string? key = equals < 0 ? null : text.Substring(start, equals - start).Trim();
            var valueStart = equals < 0 ? start : equals + 1;
            var valueEnd = position;
            while (valueStart < valueEnd && char.IsWhiteSpace(text[valueStart])) valueStart++;
            while (valueEnd > valueStart && char.IsWhiteSpace(text[valueEnd - 1])) valueEnd--;
            var value = text.Substring(valueStart, valueEnd - valueStart);
            if (value.Length >= 2 && (value[0] is '\'' or '"') && value[value.Length - 1] == value[0])
            { value = Unquote(value); valueStart++; valueEnd--; }
            if (key != null) { sawNamed = true; if (key.Length == 0 || !names.Add(key)) report(new("XG0010", "A named markup argument is empty or duplicated.", argumentSpan)); }
            else if (sawNamed) report(new("XG0010", "Positional arguments must precede named arguments.", argumentSpan));
            if (value.Length != 0 || key != null) arguments.Add(new(key, value, argumentSpan) { ValueSpan = TextSpan.FromBounds(span.Start + valueStart, span.Start + valueEnd) });
            if (position < end && text[position] == ',') position++;
        }
        return new(name, arguments.ToImmutable(), span);
    }
    private static bool NextIsNamedArgument(string text, int position, int end)
    {
        var start = position;
        while (position < end && char.IsWhiteSpace(text[position])) position++;
        if (position == start) return false;
        var nameStart = position;
        while (position < end && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or ':' or '.' or '-')) position++;
        if (position == nameStart) return false;
        while (position < end && char.IsWhiteSpace(text[position])) position++;
        return position < end && text[position] == '=';
    }
    private static string Unquote(string value)
    {
        var result = new System.Text.StringBuilder(value.Length - 2);
        for (var i = 1; i < value.Length - 1; i++) { if (value[i] == '\\' && i + 1 < value.Length - 1) i++; result.Append(value[i]); }
        return result.ToString();
    }
}
