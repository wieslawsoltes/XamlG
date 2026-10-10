using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>A data-only Avalonia binding. Paths walk JSON, never CLR objects or delegates.</summary>
internal sealed class UiBindingExpression : IUiExpression
{
    internal sealed record Spec(string Root, ImmutableArray<string> Path, string Mode,
        string? Fallback, string? TargetNull, string? Format);
    private readonly Spec _spec;
    private readonly UiProperty? _target;
    private readonly int _textLimit;
    public string Source { get; }
    private UiBindingExpression(string source, Spec spec, UiProperty? target, UiLimits limits)
    { Source = source; _spec = spec; _target = target; _textLimit = limits.TextCharacters; }
    internal static bool IsBinding(string value) => value.StartsWith("{Binding", StringComparison.Ordinal) || value.StartsWith("{CompiledBinding", StringComparison.Ordinal);
    internal static UiBindingExpression Compile(string source, string root, UiProperty? target, UiLimits limits)
    {
        var spec = Parse(source, root, limits);
        if (spec.Mode == "TwoWay") throw Invalid("TwoWay requires a direct state-slot input binding.");
        if (spec.Fallback != null) Literal(spec.Fallback, target);
        if (spec.TargetNull != null) Literal(spec.TargetNull, target);
        return new(source, spec, target, limits);
    }
    internal static Spec Parse(string source, string root, UiLimits limits)
    {
        if (source.Length > limits.ExpressionCharacters || !source.EndsWith('}')) throw Invalid("Binding exceeds its syntax budget.");
        var prefix = source.StartsWith("{CompiledBinding", StringComparison.Ordinal) ? "{CompiledBinding" : "{Binding";
        if (!source.StartsWith(prefix, StringComparison.Ordinal) || source.Length <= prefix.Length ||
            source[prefix.Length] != '}' && !char.IsWhiteSpace(source[prefix.Length])) throw Invalid("Invalid binding markup.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in Split(source[prefix.Length..^1]))
        {
            var equals = part.IndexOf('=');
            var name = equals < 0 ? "Path" : part[..equals].Trim();
            var value = equals < 0 ? part : part[(equals + 1)..].Trim();
            if (name is not ("Path" or "Mode" or "FallbackValue" or "TargetNullValue" or "StringFormat") || !options.TryAdd(name, Unquote(value)))
                throw Invalid("Duplicate or unsupported binding option: " + name);
        }
        var mode = options.GetValueOrDefault("Mode", "Default");
        if (mode is not ("Default" or "OneWay" or "TwoWay")) throw Invalid("Binding mode must be Default, OneWay or a direct state-slot TwoWay.");
        var path = ReadPath(options.GetValueOrDefault("Path", ""));
        if (path.Length != 0 && path[0] is "state" or "data" or "item") { root = path[0]; path = path.RemoveAt(0); }
        var format = options.GetValueOrDefault("StringFormat");
        if (format != null) { if (format.StartsWith("{}", StringComparison.Ordinal)) format = format[2..]; ValidateFormat(format, limits.TextCharacters); }
        return new(root, path, mode, options.GetValueOrDefault("FallbackValue"), options.GetValueOrDefault("TargetNullValue"), format);
    }
    public JsonElement Evaluate(JsonElement state, JsonElement data, JsonElement? item = null)
    {
        var value = _spec.Root switch { "state" => state, "data" => data, "item" => item ?? UiJson.Element(null), _ => throw Invalid("Unknown binding scope.") };
        var found = true;
        foreach (var member in _spec.Path)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(member, out var property)) value = property;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(member, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < value.GetArrayLength()) value = value[index];
            else { found = false; break; }
        }
        if (!found) value = _spec.Fallback != null ? Literal(_spec.Fallback, _target) : throw Invalid("The JSON binding path was not found.");
        else if (value.ValueKind == JsonValueKind.Null && _spec.TargetNull != null) value = Literal(_spec.TargetNull, _target);
        if (_spec.Format != null)
        {
            try
            {
                if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
                    value.ValueKind == JsonValueKind.String && value.GetString()!.Length > _textLimit)
                    throw Invalid("StringFormat requires a bounded scalar value.");
                var formatted = FormatValue(_spec.Format, UiJson.Value(value));
                if (formatted.Length > _textLimit) throw Invalid("Formatted binding exceeds its text budget.");
                return UiJson.Element(formatted);
            }
            catch (FormatException) { throw Invalid("Invalid binding StringFormat for this value."); }
        }
        return value.Clone();
    }
    private string FormatValue(string format, object? value)
    {
        var output = new StringBuilder();
        for (var p = 0; p < format.Length; p++)
        {
            string part;
            if (format[p] == '{' && p + 1 < format.Length && format[p + 1] == '{') { part = "{"; p++; }
            else if (format[p] == '}' && p + 1 < format.Length && format[p + 1] == '}') { part = "}"; p++; }
            else if (format[p] == '{')
            {
                var end = format.IndexOf('}', p + 1);
                part = string.Format(CultureInfo.InvariantCulture, format[p..(end + 1)], value); p = end;
            }
            else part = format[p].ToString();
            if (output.Length + part.Length > _textLimit) throw Invalid("Formatted binding exceeds its text budget.");
            output.Append(part);
        }
        return output.ToString();
    }
    private static JsonElement Literal(string text, UiProperty? target)
    {
        if (text == "{x:Null}") return UiJson.Element(null);
        return target?.ReadLiteral(text.StartsWith("{}", StringComparison.Ordinal) ? text[2..] : text) ?? UiJson.Element(text);
    }
    private static IEnumerable<string> Split(string source)
    {
        var start = 0; char quote = '\0'; var braces = 0; var brackets = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '{') braces++; else if (c == '}') braces--;
            if (c == '[') brackets++; else if (c == ']') brackets--;
            if (braces < 0 || brackets < 0) throw Invalid("Unbalanced binding syntax.");
            if (c == ',' && braces == 0 && brackets == 0)
            {
                var part = source[start..i].Trim(); if (part.Length == 0) throw Invalid("Empty binding option.");
                yield return part; start = i + 1;
            }
        }
        if (quote != '\0' || braces != 0 || brackets != 0) throw Invalid("Unbalanced binding syntax.");
        var last = source[start..].Trim();
        if (last.Length != 0) yield return last;
        else if (start != 0) throw Invalid("Trailing binding separator.");
    }
    private static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] is '\'' or '"')
        {
            if (text[^1] != text[0]) throw Invalid("Unterminated binding string.");
            return text[1..^1];
        }
        return text;
    }
    private static ImmutableArray<string> ReadPath(string source)
    {
        if (source.Length > 1024) throw Invalid("Binding path is too long.");
        if (source is "" or ".") return [];
        var path = ImmutableArray.CreateBuilder<string>(); var p = 0;
        while (p < source.Length)
        {
            if (path.Count == 64) throw Invalid("Binding path has too many segments.");
            if (source[p] == '[')
            {
                var start = ++p; char quote = '\0';
                while (p < source.Length)
                {
                    var c = source[p];
                    if (quote != '\0') { if (c == quote) quote = '\0'; }
                    else if (c is '\'' or '"') quote = c;
                    else if (c == ']') break;
                    p++;
                }
                if (p == source.Length || quote != '\0') throw Invalid("Unterminated path index.");
                var key = Unquote(source[start..p].Trim());
                if (key.Length is 0 or > 256 || key.Any(char.IsControl)) throw Invalid("Invalid path index.");
                path.Add(key); p++;
            }
            else
            {
                var start = p;
                if (!char.IsAsciiLetter(source[p]) && source[p] != '_') throw Invalid("Invalid path member.");
                while (p < source.Length && (char.IsAsciiLetterOrDigit(source[p]) || source[p] == '_')) p++;
                path.Add(source[start..p]);
            }
            if (p == source.Length) break;
            if (source[p] == '.') { if (++p == source.Length || source[p] == '[') throw Invalid("Invalid path separator."); }
            else if (source[p] != '[') throw Invalid("Binding paths cannot execute expressions.");
        }
        return path.ToImmutable();
    }
    private static void ValidateFormat(string format, int textLimit)
    {
        if (format.Length > Math.Min(1024, textLimit)) throw Invalid("StringFormat is too long.");
        try { if (System.Text.CompositeFormat.Parse(format).MinimumArgumentCount > 1) throw Invalid("StringFormat may reference only argument zero."); }
        catch (FormatException) { throw Invalid("Malformed StringFormat."); }
        // Bound alignment and precision before string.Format can allocate an oversized result.
        for (var p = 0; p < format.Length; p++)
        {
            if (format[p] != '{') continue;
            if (p + 1 < format.Length && format[p + 1] == '{') { p++; continue; }
            var end = format.IndexOf('}', p + 1); if (end < 0) throw Invalid("Malformed StringFormat.");
            var clause = format[(p + 1)..end]; var colon = clause.IndexOf(':');
            var head = colon < 0 ? clause : clause[..colon]; var comma = head.IndexOf(',');
            if (comma >= 0 && (!int.TryParse(head[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width < -1024 || width > 1024))
                throw Invalid("StringFormat alignment exceeds 1024.");
            if (colon >= 0)
            {
                var specifier = clause[(colon + 1)..];
                if (specifier.Length > 64 || specifier.Length > 1 && char.IsAsciiLetter(specifier[0]) && specifier.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 &&
                    (!int.TryParse(specifier.AsSpan(1), out var precision) || precision > 16)) throw Invalid("StringFormat precision exceeds its budget.");
            }
            p = end;
        }
    }
    private static UiException Invalid(string message) => new("invalid_binding", message);
}
internal sealed record UiBindingScope(string Root, string ContextRoot, string RepeatRoot);
