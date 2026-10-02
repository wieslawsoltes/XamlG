using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundNewExpression(IMethodSymbol Constructor, ImmutableArray<BoundExpression> Arguments, TextSpan SourceSpan) : BoundExpression(Constructor.ContainingType, SourceSpan);
