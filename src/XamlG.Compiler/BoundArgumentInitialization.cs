using Microsoft.CodeAnalysis;
namespace XamlG.Compiler;

/// <summary>Initializes a constructed final argument from an already evaluated preceding
/// argument. This explicitly represents single-evaluation dependencies in the bound IR.</summary>
public sealed record BoundArgumentInitialization(IPropertySymbol Property, int ValueArgumentIndex);
