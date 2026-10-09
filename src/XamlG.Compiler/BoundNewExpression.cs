using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundNewExpression(IMethodSymbol Constructor, ImmutableArray<BoundExpression> Arguments, TextSpan SourceSpan) : BoundExpression(Constructor.ContainingType, SourceSpan)
{
    /// <summary>Assignments evaluated in order after construction, including init-only properties.</summary>
    public ImmutableArray<BoundPropertyInitialization> Initializers { get; init; } = ImmutableArray<BoundPropertyInitialization>.Empty;
}

public sealed record BoundPropertyInitialization(IPropertySymbol Property, BoundExpression Value);
