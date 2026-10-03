using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>Materializes inherited XML namespaces and whitespace policy when a subtree changes parent.</summary>
internal static class XamlNamespaceClosure
{
    public static string MoveFragment(XamlSyntaxTree tree, XamlElementSyntax element, XamlElementSyntax parent)
    {
        var source = Scope(tree, element);
        var destination = Scope(tree, parent);
        // Ignorable namespaces only accumulate. An inherited destination policy cannot be
        // cancelled by a namespace declaration inside the moved fragment.
        if (destination.IgnoredNamespaces.Except(source.IgnoredNamespaces).Any())
            throw new InvalidOperationException("The destination introduces a markup-compatibility policy that cannot be preserved by this move.");
        var fragment = XamlSyntaxTree.Parse(tree.Text.Substring(element.Span.Start, element.Span.Length));
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in source.Bindings)
        {
            if (binding.Key == "xml") continue;
            if (!destination.Bindings.TryGetValue(binding.Key, out var mapped) || mapped != binding.Value)
                attributes[binding.Key.Length == 0 ? "xmlns" : "xmlns:" + binding.Key] = binding.Value;
        }
        if (!source.Bindings.ContainsKey(string.Empty) && destination.Bindings.TryGetValue(string.Empty, out var defaultNamespace) && defaultNamespace.Length != 0)
            attributes["xmlns"] = string.Empty;
        if (source.PreserveSpace != destination.PreserveSpace && !element.Attributes.Any(a => a.Name == "xml:space"))
            attributes["xml:space"] = source.PreserveSpace ? "preserve" : "default";
        var ignored = source.IgnoredNamespaces.Except(destination.IgnoredNamespaces).ToArray();
        if (ignored.Length != 0)
        {
            var prefix = source.Bindings.FirstOrDefault(p => p.Value == XamlNames.Compatibility && p.Key.Length != 0).Key;
            if (string.IsNullOrEmpty(prefix))
            {
                prefix = "mc";
                while (source.Bindings.ContainsKey(prefix)) prefix += "_";
                attributes["xmlns:" + prefix] = XamlNames.Compatibility;
            }
            var existing = element.Attributes.FirstOrDefault(a => source.Expand(a.Name, true) is { Namespace: XamlNames.Compatibility, LocalName: "Ignorable" })?.Value ?? string.Empty;
            var names = source.Bindings.Where(p => p.Key.Length != 0 && ignored.Contains(p.Value)).Select(p => p.Key);
            attributes[prefix + ":Ignorable"] = string.Join(" ", new[] { existing }.Concat(names).Where(n => n.Length != 0));
        }
        if (attributes.Count == 0) return fragment.Text;
        var transaction = XamlBatchDesignerEdits.SetProperties(fragment, fragment.Root!, attributes);
        return fragment.WithChanges(transaction.Changes, fragment.Version).Text;
    }
    private static NamespaceScope Scope(XamlSyntaxTree tree, XamlElementSyntax node)
    {
        var scope = NamespaceScope.Empty;
        foreach (var ancestor in tree.Root!.DescendantsAndSelf().Where(e => e.Span.Contains(node.Span)).OrderByDescending(e => e.Span.Length)) scope = scope.Push(ancestor);
        return scope;
    }
}
