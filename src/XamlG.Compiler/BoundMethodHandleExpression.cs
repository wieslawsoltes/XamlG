using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>A runtime method token for a statically resolved nongeneric method.</summary>
public sealed record BoundMethodHandleExpression(IMethodSymbol Method, ITypeSymbol HandleType, TextSpan SourceSpan)
    : BoundExpression(HandleType, SourceSpan);
