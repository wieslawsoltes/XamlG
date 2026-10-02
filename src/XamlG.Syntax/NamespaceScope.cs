using System.Collections.Immutable;
namespace XamlG.Syntax;

/// <summary>Persistent namespace and XML-space scope. A child never mutates its parent.</summary>
public sealed class NamespaceScope
{
    public static NamespaceScope Empty { get; } = new(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal).Add("xml", XamlNames.Xml), ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal), false);
    private NamespaceScope(ImmutableDictionary<string, string> bindings, ImmutableHashSet<string> ignored, bool preserve)
    { Bindings = bindings; IgnoredNamespaces = ignored; PreserveSpace = preserve; }
    public ImmutableDictionary<string, string> Bindings { get; }
    public ImmutableHashSet<string> IgnoredNamespaces { get; }
    public bool PreserveSpace { get; }
    public NamespaceScope Push(XamlElementSyntax element)
    {
        var bindings = Bindings; var ignored = IgnoredNamespaces; var preserve = PreserveSpace;
        foreach (var a in element.Attributes)
            if (a.IsNamespace) bindings = bindings.SetItem(a.Name == "xmlns" ? string.Empty : a.Name.Substring(6), a.Value);
        var result = new NamespaceScope(bindings, ignored, preserve);
        foreach (var a in element.Attributes)
        {
            var name = result.Expand(a.Name, true);
            if (name.Namespace == XamlNames.Xml && name.LocalName == "space") preserve = a.Value == "preserve" || a.Value != "default" && preserve;
            if (name.Namespace == XamlNames.Compatibility && name.LocalName == "Ignorable")
                foreach (var prefix in a.Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (bindings.TryGetValue(prefix, out var ns)) ignored = ignored.Add(ns);
        }
        return new NamespaceScope(bindings, ignored, preserve);
    }
    public ExpandedName Expand(string name, bool attribute = false)
    {
        var colon = name.IndexOf(':');
        if (colon < 0) return new(attribute ? string.Empty : Bindings.TryGetValue(string.Empty, out var ns) ? ns : string.Empty, name);
        return new(Bindings.TryGetValue(name.Substring(0, colon), out var mapped) ? mapped : null, name.Substring(colon + 1));
    }
    public XamlAttributeSyntax? Directive(XamlElementSyntax element, string name)
    {
        foreach (var attribute in element.Attributes)
        {
            var expanded = Expand(attribute.Name, true);
            if (expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace) && expanded.LocalName == name) return attribute;
        }
        return null;
    }
}
