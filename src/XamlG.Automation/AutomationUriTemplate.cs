namespace XamlG.Automation;

/// <summary>Bounded simple URI templates. Each {name} is one percent-encoded value;
/// callers encode document paths, including slashes, with Uri.EscapeDataString.</summary>
public static partial class AutomationUriTemplate
{
    /// <summary>Matches the entire URI using bounded suffix feasibility, without regex
    /// backtracking or elapsed-time budgets. Adjacent variables retain greedy capture semantics.</summary>
    public static IReadOnlyDictionary<string, string>? Match(string template, string uri)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(uri);
        if (template.Length > 2048 || uri.Length > 4096) return null;
        var tokens = new List<Token>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var literalStart = 0;
        for (var position = 0; position < template.Length; position++)
        {
            if (template[position] != '{' || position + 1 >= template.Length || !char.IsAsciiLetter(template[position + 1])) continue;
            var end = position + 2;
            while (end < template.Length && (char.IsAsciiLetterOrDigit(template[end]) || template[end] == '_')) end++;
            if (end >= template.Length || template[end] != '}') continue;
            var name = template[(position + 1)..end];
            if (names.Count >= 8 || !names.Add(name)) return null;
            if (position != literalStart) tokens.Add(new(template[literalStart..position], false));
            tokens.Add(new(name, true));
            position = end; literalStart = end + 1;
        }
        if (literalStart < template.Length) tokens.Add(new(template[literalStart..], false));

        // At most seventeen token rows and 4,097 columns. A variable consumes one
        // or more non-delimiter characters; the next row determines legal endpoints.
        var feasible = new bool[tokens.Count + 1][];
        feasible[^1] = new bool[uri.Length + 1];
        feasible[^1][uri.Length] = true;
        for (var index = tokens.Count - 1; index >= 0; index--)
        {
            var token = tokens[index]; var next = feasible[index + 1];
            var row = feasible[index] = new bool[uri.Length + 1];
            if (token.Variable)
            {
                for (var position = uri.Length - 1; position >= 0; position--)
                    row[position] = IsValueCharacter(uri[position]) && (next[position + 1] || row[position + 1]);
            }
            else
            {
                for (var position = 0; position <= uri.Length - token.Text.Length; position++)
                    row[position] = next[position + token.Text.Length] && uri.AsSpan(position, token.Text.Length).SequenceEqual(token.Text.AsSpan());
            }
        }
        if (!feasible[0][0]) return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var cursor = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (!token.Variable) { cursor += token.Text.Length; continue; }
            var end = cursor;
            while (end < uri.Length && IsValueCharacter(uri[end])) end++;
            while (end > cursor && !feasible[index + 1][end]) end--;
            if (end == cursor) return null; // Defensive: feasibility guarantees a nonempty capture.
            result.Add(token.Text, Uri.UnescapeDataString(uri[cursor..end]));
            cursor = end;
        }
        return result;
    }

    public static bool IsMatch(AutomationResource resource, string uri)
        => resource.IsTemplate ? Match(resource.Uri, uri) != null : resource.Uri == uri;

    private static bool IsValueCharacter(char value) => value is not ('/' or '?' or '#');
    private readonly record struct Token(string Text, bool Variable);
}
