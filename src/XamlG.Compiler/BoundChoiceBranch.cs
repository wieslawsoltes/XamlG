using Microsoft.CodeAnalysis;
namespace XamlG.Compiler;

/// <summary>A statically resolved predicate and a value evaluated only when it matches.</summary>
public sealed record BoundChoiceBranch(IMethodSymbol Predicate, BoundExpression Option, BoundExpression Value);
