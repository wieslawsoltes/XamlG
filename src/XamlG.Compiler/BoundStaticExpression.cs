using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundStaticExpression(ISymbol Member, ITypeSymbol ValueType, TextSpan SourceSpan) : BoundExpression(ValueType, SourceSpan);
