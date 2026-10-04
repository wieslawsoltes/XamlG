using Microsoft.CodeAnalysis;
using XamlG.Compiler.Resources;

namespace XamlG.CSharp.Resources;

internal static class ResourceCatalogEquivalence
{
    public static bool Equals(XamlResourceCatalog left, XamlResourceCatalog right)
    {
        var first = left.Resources.OrderBy(Key, StringComparer.Ordinal).ToArray();
        var second = right.Resources.OrderBy(Key, StringComparer.Ordinal).ToArray();
        if (first.Length != second.Length) return false;
        for (var i = 0; i < first.Length; i++)
        {
            var a = first[i]; var b = second[i];
            if (a.Uri != b.Uri || a.LocalDocumentId != b.LocalDocumentId || a.GeneratedNamespace != b.GeneratedNamespace ||
                a.LocalFactoryMethod != b.LocalFactoryMethod ||
                !SymbolEqualityComparer.Default.Equals(a.LocalFactoryType, b.LocalFactoryType) ||
                !SymbolEqualityComparer.Default.Equals(a.RootType, b.RootType) ||
                !SymbolEqualityComparer.Default.Equals(a.ExternalFactory, b.ExternalFactory)) return false;
        }
        return true;
    }
    private static string Key(XamlResourceDescriptor descriptor) => descriptor.Uri + "\0" + descriptor.LocalDocumentId + "\0" +
        descriptor.RootType.ToDisplayString() + "\0" + descriptor.RootType.ContainingAssembly.Identity + "\0" + descriptor.ExternalFactory?.ToDisplayString();
}
