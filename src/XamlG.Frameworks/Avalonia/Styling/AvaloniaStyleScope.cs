using Microsoft.CodeAnalysis;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia.Styling;

internal static class AvaloniaStyleScope
{
    private static readonly string[] TemplateScopeAttributes = { AvaloniaStyleMetadata.ControlTemplateScope };

    public static bool Is(ITypeSymbol type, string metadataName)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.HasMetadataName(metadataName)) return true;
        return type.AllInterfaces.Any(contract => contract.HasMetadataName(metadataName));
    }

    public static bool IsTemplate(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.HasAttribute(TemplateScopeAttributes)) return true;
        return type.AllInterfaces.Any(contract => contract.HasAttribute(TemplateScopeAttributes));
    }
}
