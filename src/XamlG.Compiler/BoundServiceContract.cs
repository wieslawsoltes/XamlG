using Microsoft.CodeAnalysis;
namespace XamlG.Compiler;
public sealed record BoundServiceContract(XamlServiceMapping Mapping, INamedTypeSymbol InterfaceType, INamedTypeSymbol? NamespaceItemType);
