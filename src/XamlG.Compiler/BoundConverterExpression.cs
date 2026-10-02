using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundConverterExpression(string Text, INamedTypeSymbol Converter, ITypeSymbol ValueType, TextSpan SourceSpan) : BoundExpression(ValueType, SourceSpan);
