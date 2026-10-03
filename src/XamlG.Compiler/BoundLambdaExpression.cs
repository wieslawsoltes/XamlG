using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>A typed expression-bodied delegate, independent of a C# textual representation.</summary>
public sealed record BoundLambdaExpression(INamedTypeSymbol DelegateType,
    ImmutableArray<BoundParameterExpression> Parameters, BoundExpression Body, bool IsStatic, TextSpan SourceSpan)
    : BoundExpression(DelegateType, SourceSpan);
