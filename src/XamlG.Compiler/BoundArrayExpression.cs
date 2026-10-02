using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundArrayExpression(ImmutableArray<BoundExpression> Values, IArrayTypeSymbol ArrayType, TextSpan SourceSpan) : BoundExpression(ArrayType, SourceSpan);
