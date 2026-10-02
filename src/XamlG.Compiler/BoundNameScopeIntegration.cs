using Microsoft.CodeAnalysis;
namespace XamlG.Compiler;
/// <summary>Validated framework name-scope operations, bound to symbols rather than emitted from unchecked names.</summary>
public sealed record BoundNameScopeIntegration(INamedTypeSymbol ConcreteType, INamedTypeSymbol ContractType,
    IMethodSymbol Register, IMethodSymbol Complete, IMethodSymbol? Attach);
