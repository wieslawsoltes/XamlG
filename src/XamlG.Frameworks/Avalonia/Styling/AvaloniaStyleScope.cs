using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using System.Runtime.CompilerServices;

namespace XamlG.Frameworks.Avalonia.Styling;

internal static class AvaloniaStyleScope
{
    private static readonly string[] TemplateScopeAttributes = { AvaloniaStyleMetadata.ControlTemplateScope };
    private static readonly ConditionalWeakTable<ITypeSymbol, TemplateScope> TemplateScopes = new();
    private sealed class TemplateScope(bool value) { public bool Value { get; } = value; }

    public static bool Is(ITypeSymbol type, string metadataName)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.HasMetadataName(metadataName)) return true;
        return type.AllInterfaces.Any(contract => contract.HasMetadataName(metadataName));
    }

    public static bool IsTemplate(ITypeSymbol type) => TemplateScopes.GetValue(type, static type => new(FindTemplateScope(type))).Value;
    private static bool FindTemplateScope(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.HasAttribute(TemplateScopeAttributes)) return true;
        return type.AllInterfaces.Any(contract => contract.HasAttribute(TemplateScopeAttributes));
    }
}
