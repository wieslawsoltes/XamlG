using System.Text;
using System.Text.RegularExpressions;

namespace XamlG.Automation;

/// <summary>Bounded simple URI templates. Each {name} is one percent-encoded value;
/// callers encode document paths, including slashes, with Uri.EscapeDataString.</summary>
public static partial class AutomationUriTemplate
{
    public static IReadOnlyDictionary<string, string>? Match(string template, string uri)
    {
        if (template.Length > 2048 || uri.Length > 4096) return null;
        var names = new List<string>(); var pattern = new StringBuilder("^"); var offset = 0;
        foreach (Match token in Variable().Matches(template))
        {
            if (names.Count >= 8 || names.Contains(token.Groups[1].Value, StringComparer.Ordinal)) return null;
            names.Add(token.Groups[1].Value); pattern.Append(Regex.Escape(template[offset..token.Index])).Append("([^/?#]+)");
            offset = token.Index + token.Length;
        }
        pattern.Append(Regex.Escape(template[offset..])).Append('$');
        var match = Regex.Match(uri, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (!match.Success) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++) result.Add(names[i], Uri.UnescapeDataString(match.Groups[i + 1].Value));
        return result;
    }
    public static bool IsMatch(AutomationResource resource, string uri) => resource.IsTemplate ? Match(resource.Uri, uri) != null : resource.Uri == uri;
    [GeneratedRegex("\\{([a-zA-Z][a-zA-Z0-9_]*)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();
}
