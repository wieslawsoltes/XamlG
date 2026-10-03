using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

public sealed record BoundCallAssignment(IMethodSymbol Method, ImmutableArray<BoundExpression> Arguments,
    bool IncludeTarget, TextSpan SourceSpan) : BoundAssignment(SourceSpan)
{
    public bool OwnResult { get; init; }
    public BoundExpression? TargetDescriptor { get; init; }
}
