using Microsoft.CodeAnalysis;

namespace XamlG.Compiler;

/// <summary>Resolves framework-supplied converter metadata without loading types or executing application code.</summary>
public interface IXamlTypeConverterProvider
{
    INamedTypeSymbol? GetConverter(BindingContext context, ITypeSymbol targetType);
}
