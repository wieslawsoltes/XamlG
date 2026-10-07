using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>A typed delegate conversion. A null receiver denotes the current document root
/// for instance methods, including the owner root inside deferred content.</summary>
public sealed record BoundMethodGroupExpression(IMethodSymbol Method, BoundExpression? Receiver,
    INamedTypeSymbol DelegateType, TextSpan SourceSpan) : BoundExpression(DelegateType, SourceSpan);
