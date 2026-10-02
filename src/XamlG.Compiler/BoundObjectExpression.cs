using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundObjectExpression(BoundObject Object) : BoundExpression(Object.Type, Object.Syntax.Span);
