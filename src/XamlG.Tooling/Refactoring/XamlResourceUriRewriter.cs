using XamlG.Compiler.Resources;

namespace XamlG.Tooling.Refactoring;

internal static class XamlResourceUriRewriter
{
    public static string? Rewrite(string original, string newBase, string target)
    {
        if (XamlResourceUri.Resolve(newBase, original) == target) return null;
        var destination = new Uri(target, UriKind.Absolute);
        var parent = new Uri(newBase, UriKind.Absolute);
        var sameAuthority = parent.Scheme == destination.Scheme && parent.Authority == destination.Authority;
        if (Uri.TryCreate(original, UriKind.Absolute, out var absolute) && !absolute.IsFile || !sameAuthority) return target;
        if (original.StartsWith("/", StringComparison.Ordinal))
            return "/" + destination.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var relative = parent.MakeRelativeUri(destination).OriginalString;
        // A colon in the first segment would be interpreted as a URI scheme.
        if (relative.Split('/')[0].Contains(':') || original.StartsWith("./", StringComparison.Ordinal) && !relative.StartsWith(".", StringComparison.Ordinal))
            relative = "./" + relative;
        return relative;
    }
    public static string Escape(string value, char? quote)
    {
        value = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return quote == '\'' ? value.Replace("'", "&apos;") : quote == '"' ? value.Replace("\"", "&quot;") : value;
    }
}
