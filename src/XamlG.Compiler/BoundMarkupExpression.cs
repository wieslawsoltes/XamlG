using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundMarkupExpression(BoundObject Extension, IMethodSymbol Method, ITypeSymbol ValueType, TextSpan SourceSpan) : BoundExpression(ValueType, SourceSpan);
