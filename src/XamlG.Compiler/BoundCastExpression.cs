using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundCastExpression(BoundExpression Value, ITypeSymbol TargetType, TextSpan SourceSpan) : BoundExpression(TargetType, SourceSpan);
