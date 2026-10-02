using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundReferenceExpression(string Name, ITypeSymbol? ValueType, TextSpan SourceSpan) : BoundExpression(ValueType, SourceSpan);
