using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;

/// <summary>An ordered, lazy decision with one shared extension receiver. Unselected
/// values are never constructed, evaluated, or attached to a runtime session.</summary>
public sealed record BoundChoiceExpression(BoundObject Extension, ImmutableArray<BoundChoiceBranch> Branches,
    BoundExpression? Default, ITypeSymbol ResultType, TextSpan SourceSpan) : BoundExpression(ResultType, SourceSpan);
