using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;

/// <summary>A collection literal constructed before capacity assignment and ordered item evaluation.</summary>
public sealed record BoundCollectionExpression(IMethodSymbol Constructor, IMethodSymbol AddMethod,
    IPropertySymbol Capacity, ImmutableArray<BoundExpression> Values, TextSpan SourceSpan)
    : BoundExpression(Constructor.ContainingType, SourceSpan);
