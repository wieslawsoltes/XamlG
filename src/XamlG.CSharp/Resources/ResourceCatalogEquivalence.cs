using Microsoft.CodeAnalysis;
using XamlG.Compiler.Resources;

namespace XamlG.CSharp.Resources;

internal static class ResourceCatalogEquivalence
{
    public static bool Equals(XamlResourceCatalog left, XamlResourceCatalog right)
    {
        if (ReferenceEquals(left, right)) return true;
        // A counted multiset preserves duplicate-export ambiguity. Compare actual
        // symbols and every factory field, not repeatedly formatted display keys.
        var counts = new Dictionary<XamlResourceDescriptor, int>(DescriptorComparer.Instance);
        foreach (var descriptor in left.Resources)
        {
            counts.TryGetValue(descriptor, out var count);
            counts[descriptor] = count + 1;
        }
        foreach (var descriptor in right.Resources)
        {
            if (!counts.TryGetValue(descriptor, out var count)) return false;
            if (count == 1) counts.Remove(descriptor);
            else counts[descriptor] = count - 1;
        }
        return counts.Count == 0;
    }

    private sealed class DescriptorComparer : IEqualityComparer<XamlResourceDescriptor>
    {
        public static readonly DescriptorComparer Instance = new();
        public bool Equals(XamlResourceDescriptor? a, XamlResourceDescriptor? b) => ReferenceEquals(a, b) ||
            a != null && b != null && a.Uri == b.Uri && a.LocalDocumentId == b.LocalDocumentId &&
            a.GeneratedNamespace == b.GeneratedNamespace && a.LocalFactoryMethod == b.LocalFactoryMethod &&
            SymbolEqualityComparer.Default.Equals(a.RootType, b.RootType) &&
            SymbolEqualityComparer.Default.Equals(a.LocalFactoryType, b.LocalFactoryType) &&
            SymbolEqualityComparer.Default.Equals(a.ExternalFactory, b.ExternalFactory);
        public int GetHashCode(XamlResourceDescriptor value)
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(value.Uri);
                hash = hash * 397 ^ (value.LocalDocumentId?.GetHashCode() ?? 0);
                hash = hash * 397 ^ (value.GeneratedNamespace?.GetHashCode() ?? 0);
                hash = hash * 397 ^ (value.LocalFactoryMethod?.GetHashCode() ?? 0);
                hash = hash * 397 ^ SymbolEqualityComparer.Default.GetHashCode(value.RootType);
                hash = hash * 397 ^ (value.LocalFactoryType == null ? 0 : SymbolEqualityComparer.Default.GetHashCode(value.LocalFactoryType));
                return hash * 397 ^ (value.ExternalFactory == null ? 0 : SymbolEqualityComparer.Default.GetHashCode(value.ExternalFactory));
            }
        }
    }
}
