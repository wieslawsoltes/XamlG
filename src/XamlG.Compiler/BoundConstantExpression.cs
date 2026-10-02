using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundConstantExpression(object? Value, ITypeSymbol? ValueType, TextSpan SourceSpan) : BoundExpression(ValueType, SourceSpan);
