using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundAddAssignment(BoundMember? Collection, IMethodSymbol AddMethod, ImmutableArray<BoundExpression> Arguments, TextSpan SourceSpan) : BoundAssignment(SourceSpan)
{
    public ImmutableArray<BoundArgumentInitialization> ValueInitializers { get; init; } = ImmutableArray<BoundArgumentInitialization>.Empty;
    /// <summary>Ordered runtime alternatives for an object-valued last argument. Empty for a statically selected call.</summary>
    public ImmutableArray<IMethodSymbol> Alternatives { get; init; } = ImmutableArray<IMethodSymbol>.Empty;
}
