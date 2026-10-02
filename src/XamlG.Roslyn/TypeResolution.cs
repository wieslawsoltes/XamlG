using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
namespace XamlG.Roslyn;
public sealed record TypeResolution(INamedTypeSymbol? Type, ImmutableArray<INamedTypeSymbol> Candidates)
{
    public bool IsAmbiguous => Candidates.Length > 1;
    public static TypeResolution Missing { get; } = new(null, ImmutableArray<INamedTypeSymbol>.Empty);
}
