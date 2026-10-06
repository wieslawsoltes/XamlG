namespace XamlG.Runtime;

/// <summary>Canonical runtime counterpart of the compiler's resource-address policy. This
/// performs URI arithmetic only; it never probes files, assemblies, assets or the network.</summary>
public static class XamlCompiledResourceAddress
{
    public static Uri Absolute(Uri uri, Uri? baseUri = null)
    {
        if (uri == null) throw new ArgumentNullException(nameof(uri));
        if (uri.IsAbsoluteUri) return uri;
        if (baseUri == null || !baseUri.IsAbsoluteUri) throw new ArgumentException("A relative compiled resource requires an absolute base URI.", nameof(baseUri));
        return new Uri(baseUri, uri);
    }
    public static string Key(Uri absolute)
    {
        if (absolute == null) throw new ArgumentNullException(nameof(absolute));
        if (!absolute.IsAbsoluteUri || absolute.IsFile || absolute.Host.Length == 0 || absolute.UserInfo.Length != 0 ||
            !absolute.IsDefaultPort || absolute.Query.Length != 0 || absolute.Fragment.Length != 0)
            throw new ArgumentException("A compiled resource needs a scheme, assembly authority and path without credentials, ports, query or fragment.", nameof(absolute));
        var path = absolute.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var segments = path.Split('/').Select(segment =>
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Length == 0 || decoded == "." || decoded == ".." || decoded.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
                throw new ArgumentException("Invalid compiled-resource path segment.", nameof(absolute));
            return Uri.EscapeDataString(decoded);
        });
        return absolute.Scheme.ToLowerInvariant() + "://" + absolute.Host.ToLowerInvariant() + "/" + string.Join("/", segments);
    }
}
