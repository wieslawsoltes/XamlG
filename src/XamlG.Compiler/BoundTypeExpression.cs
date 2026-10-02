using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundTypeExpression(ITypeSymbol ReferencedType, ITypeSymbol TypeType, TextSpan SourceSpan) : BoundExpression(TypeType, SourceSpan);
