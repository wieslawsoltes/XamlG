using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundServiceExpression(ITypeSymbol ServiceType, TextSpan SourceSpan) : BoundExpression(ServiceType, SourceSpan);
