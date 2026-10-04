using System.Collections.Immutable;

namespace XamlG.Compiler.Resources;

/// <summary>An immutable project/reference resource index. Duplicate exports are ambiguous, never first-match wins.</summary>
public sealed class XamlResourceCatalog : IXamlResourceResolver
{
    private readonly ImmutableDictionary<string, ImmutableArray<XamlResourceDescriptor>> _resources;
    public XamlResourceCatalog(IEnumerable<XamlResourceDescriptor> resources)
    {
        if (resources == null) throw new ArgumentNullException(nameof(resources));
        _resources = resources.GroupBy(r => XamlResourceUri.Normalize(r.Uri), StringComparer.Ordinal)
            .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.Ordinal);
    }
    public IEnumerable<XamlResourceDescriptor> Resources => _resources.Values.SelectMany(v => v);
    public XamlResourceLookup Resolve(string? baseUri, string source)
    {
        string uri;
        try { uri = XamlResourceUri.Resolve(baseUri, source); }
        catch (ArgumentException error) { return new(null, error.Message); }
        if (!_resources.TryGetValue(uri, out var matches)) return new(null, "No compiled resource is exported at '" + uri + "'. Include it in the project or reference its XamlG-compiled assembly.");
        if (matches.Length != 1) return new(null, "Compiled resource '" + uri + "' is ambiguous (" + matches.Length + " exports).");
        return new(matches[0], null);
    }
}
