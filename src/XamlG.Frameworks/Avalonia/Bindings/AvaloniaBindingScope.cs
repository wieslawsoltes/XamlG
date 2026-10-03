using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed record AvaloniaBindingScope(INamedTypeSymbol? DataType, bool CompileBindings)
{
    public static readonly XamlAnnotationKey<AvaloniaBindingScope> Key = new("Avalonia.BindingScope");
}
