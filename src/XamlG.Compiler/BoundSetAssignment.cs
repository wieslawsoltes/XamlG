using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundSetAssignment(BoundMember Member, BoundExpression Value, TextSpan SourceSpan) : BoundAssignment(SourceSpan);
