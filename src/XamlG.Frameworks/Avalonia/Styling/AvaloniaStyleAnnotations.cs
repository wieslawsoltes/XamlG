using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Styling;

internal static class AvaloniaStyleAnnotations
{
    public static readonly XamlAnnotationKey<INamedTypeSymbol> TargetType = new("Avalonia.StyleTargetType");
    public static readonly XamlAnnotationKey<BoundSelector> Selector = new("Avalonia.BoundSelector");
    public static readonly XamlAnnotationKey<RegisteredProperty> SetterProperty = new("Avalonia.SetterProperty");
    public static readonly XamlAnnotationKey<HashSet<ISymbol>> AssignedProperties = new("Avalonia.AssignedStyleProperties");
}
