using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

public sealed record BoundMethodGroupExpression(IMethodSymbol Method, BoundExpression? Receiver,
    INamedTypeSymbol DelegateType, TextSpan SourceSpan) : BoundExpression(DelegateType, SourceSpan);
