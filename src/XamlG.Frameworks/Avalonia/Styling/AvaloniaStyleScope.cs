using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using System.Runtime.CompilerServices;

namespace XamlG.Frameworks.Avalonia.Styling;

internal static class AvaloniaStyleScope
{
    private static readonly string[] TemplateScopeAttributes = { AvaloniaStyleMetadata.ControlTemplateScope };
    private static readonly ConditionalWeakTable<ITypeSymbol, TemplateScope> TemplateScopes = new();
    private sealed class TemplateScope(bool value) { public bool Value { get; } = value; }
    private static readonly ConditionalWeakTable<ITypeSymbol, HashSet<string>> Hierarchies = new();

    public static bool Is(ITypeSymbol type, string metadataName) =>
        Hierarchies.GetValue(type, static value => ReadHierarchy(value)).Contains(metadataName);

    private static HashSet<string> ReadHierarchy(ITypeSymbol type)
    {
        // Property rules repeatedly ask the same classification questions. Build
        // the immutable name set once instead of walking every base/interface
        // for each property. Values hold no symbols; weak keys release snapshots.
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            names.Add(current.MetadataName());
        foreach (var contract in type.AllInterfaces) names.Add(contract.MetadataName());
        return names;
    }

    public static bool IsTemplate(ITypeSymbol type) => TemplateScopes.GetValue(type, static type => new(FindTemplateScope(type))).Value;
    private static bool FindTemplateScope(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.HasAttribute(TemplateScopeAttributes)) return true;
        return type.AllInterfaces.Any(contract => contract.HasAttribute(TemplateScopeAttributes));
    }
}
