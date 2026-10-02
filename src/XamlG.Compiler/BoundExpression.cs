using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public abstract record BoundExpression(ITypeSymbol? Type, TextSpan Span);
