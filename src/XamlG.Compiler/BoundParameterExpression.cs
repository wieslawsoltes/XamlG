using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

public sealed record BoundParameterExpression(string Name, ITypeSymbol ParameterType, TextSpan SourceSpan)
    : BoundExpression(ParameterType, SourceSpan);
