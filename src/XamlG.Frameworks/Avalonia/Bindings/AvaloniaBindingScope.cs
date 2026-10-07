using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed record AvaloniaBindingScope(ITypeSymbol? DataType, bool CompileBindings)
{
    public static readonly XamlAnnotationKey<AvaloniaBindingScope> Key = new("Avalonia.BindingScope");
    public bool HasDataTypeMetadata { get; init; }
}
