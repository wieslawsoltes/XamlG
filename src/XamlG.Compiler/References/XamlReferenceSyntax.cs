using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler.References;

/// <summary>Reference discovery follows the compiler's ignored-namespace policy.</summary>
public static class XamlReferenceSyntax
{
    public static bool IsRuntimeElement(XamlElementSyntax element, NamespaceScope scope, RoslynTypeSystem types) =>
        !IsIgnored(scope.Expand(element.Name).Namespace, scope, types);
    public static bool IsRuntimeAttribute(XamlAttributeSyntax attribute, NamespaceScope scope, RoslynTypeSystem types) =>
        !attribute.IsNamespace && !IsIgnored(scope.Expand(attribute.Name, true).Namespace, scope, types);
    private static bool IsIgnored(string? ns, NamespaceScope scope, RoslynTypeSystem types) => ns != null &&
        (ns == XamlNames.Compatibility || scope.IgnoredNamespaces.Contains(ns) || types.Configuration.IgnoredNamespaces.Contains(ns));
}
