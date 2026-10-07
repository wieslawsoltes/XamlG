using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Converts an evaluated value with the current type-descriptor context.</summary>
public sealed record BoundValueConverterExpression(BoundExpression Value, INamedTypeSymbol Converter, ITypeSymbol ValueType, TextSpan SourceSpan)
    : BoundExpression(ValueType, SourceSpan);
