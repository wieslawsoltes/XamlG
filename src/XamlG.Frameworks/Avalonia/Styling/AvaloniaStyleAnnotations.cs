using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Styling;

internal static class AvaloniaStyleAnnotations
{
    public static readonly XamlAnnotationKey<INamedTypeSymbol> TargetType = new("Avalonia.StyleTargetType");
    public static readonly XamlAnnotationKey<INamedTypeSymbol> TemplateTarget = new("Avalonia.TemplateTarget");
    public static readonly XamlAnnotationKey<INamedTypeSymbol> DetachedTemplateTarget = new("Avalonia.DetachedTemplateTarget");
    public static readonly XamlAnnotationKey<BoundSelector> Selector = new("Avalonia.BoundSelector");
    public static readonly XamlAnnotationKey<AvaloniaPropertyReference> SetterProperty = new("Avalonia.SetterProperty");
    public static readonly XamlAnnotationKey<AvaloniaSetterScope> SetterScope = new("Avalonia.SetterScope");
    public static readonly XamlAnnotationKey<INamedTypeSymbol> SetterTarget = new("Avalonia.SetterTarget");
}
