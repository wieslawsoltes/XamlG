using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundCallExpression(IMethodSymbol Method, BoundExpression? Receiver, ImmutableArray<BoundExpression> Arguments, TextSpan SourceSpan) : BoundExpression(Method.ReturnType, SourceSpan);
