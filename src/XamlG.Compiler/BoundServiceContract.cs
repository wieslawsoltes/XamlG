using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace XamlG.Compiler;

public sealed record BoundServiceContract(XamlServiceMapping Mapping, INamedTypeSymbol InterfaceType, INamedTypeSymbol? NamespaceItemType)
{
    public ImmutableArray<BoundServiceProperty> Properties { get; init; } = ImmutableArray<BoundServiceProperty>.Empty;
    public IPropertySymbol? NamespaceNameProperty { get; init; }
    public IPropertySymbol? AssemblyNameProperty { get; init; }
}
