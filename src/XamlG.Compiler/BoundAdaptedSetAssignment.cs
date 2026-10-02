using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
/// <summary>Evaluates once, adapts selected runtime value types, otherwise assigns the declared member type.</summary>
public sealed record BoundAdaptedSetAssignment(BoundMember Member, BoundExpression Value,
    ImmutableArray<ITypeSymbol> AdaptedTypes, IMethodSymbol Adapter, TextSpan SourceSpan) : BoundAssignment(SourceSpan);
