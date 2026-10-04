namespace XamlG.Compiler.Resources;

/// <summary>Canonical resource addresses. Authority is case-insensitive; escaped resource paths are ordinal.</summary>
public static class XamlResourceUri
{
    public static string Create(string scheme, string assemblyName, string logicalPath)
    {
        if (!Uri.CheckSchemeName(scheme)) throw new ArgumentException("Invalid resource URI scheme.", nameof(scheme));
        if (string.IsNullOrWhiteSpace(assemblyName) || assemblyName.IndexOfAny(new[] { '/', '\\', ':', '@', '?', '#', '%' }) >= 0)
            throw new ArgumentException("Invalid resource assembly identity.", nameof(assemblyName));
        if (string.IsNullOrWhiteSpace(logicalPath)) throw new ArgumentException("A logical resource path is required.", nameof(logicalPath));
        var path = logicalPath.Replace('\\', '/').TrimStart('/');
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':')))
            throw new ArgumentException("Logical paths must be normalized project-relative paths.", nameof(logicalPath));
        return Normalize(scheme + "://" + assemblyName + "/" + string.Join("/", parts.Select(Uri.EscapeDataString)));
    }

    public static string Resolve(string? baseUri, string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A resource Source is required.", nameof(source));
        if (source.IndexOf('\\') >= 0) throw new ArgumentException("Resource URIs use '/', not backslashes.", nameof(source));
        if (source.StartsWith("//", StringComparison.Ordinal)) throw new ArgumentException("A resource authority must include an explicit scheme.", nameof(source));
        if (Uri.TryCreate(source, UriKind.Absolute, out var absolute) && !absolute.IsFile)
            return Normalize(absolute.AbsoluteUri);
        if (baseUri == null || !Uri.TryCreate(baseUri, UriKind.Absolute, out var parent))
            throw new ArgumentException("A relative include requires the containing document's absolute resource URI.", nameof(baseUri));
        if (source.IndexOf(':') >= 0 && !source.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("The resource Source is not a valid relative URI.", nameof(source));
        return Normalize(new Uri(parent, source).AbsoluteUri);
    }

    public static string Normalize(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.IsFile || uri.Host.Length == 0 ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("A resource URI must have a scheme, assembly authority and path, without credentials, ports, query or fragment.", nameof(value));
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (path.Length == 0) throw new ArgumentException("A resource URI must identify a document.", nameof(value));
        // Decode and re-escape each segment so equivalent percent encodings have one identity.
        var normalized = path.Split('/').Select(segment =>
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Length == 0 || decoded is "." or ".." || decoded.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
                throw new ArgumentException("Invalid escaped resource path segment.", nameof(value));
            return Uri.EscapeDataString(decoded);
        });
        return uri.Scheme.ToLowerInvariant() + "://" + uri.Host.ToLowerInvariant() + "/" + string.Join("/", normalized);
    }
}
