using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundAddAssignment(BoundMember? Collection, IMethodSymbol AddMethod, ImmutableArray<BoundExpression> Arguments, TextSpan SourceSpan) : BoundAssignment(SourceSpan);
