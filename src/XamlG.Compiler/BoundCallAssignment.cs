using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

public sealed record BoundCallAssignment(IMethodSymbol Method, ImmutableArray<BoundExpression> Arguments,
    bool IncludeTarget, TextSpan SourceSpan) : BoundAssignment(SourceSpan)
{
    public BoundPostCall? PostCall { get; init; }
    public ImmutableArray<BoundArgumentInitialization> ValueInitializers { get; init; } = ImmutableArray<BoundArgumentInitialization>.Empty;
    public bool OwnResult { get; init; }
    public BoundExpression? TargetDescriptor { get; init; }
}
