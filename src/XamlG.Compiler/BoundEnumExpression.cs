using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundEnumExpression(ImmutableArray<IFieldSymbol> Fields, ITypeSymbol EnumType, TextSpan SourceSpan) : BoundExpression(EnumType, SourceSpan);
