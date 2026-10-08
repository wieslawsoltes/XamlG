using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Evaluates once, adapts selected runtime types, otherwise assigns the declared member type.</summary>
public sealed record BoundAdaptedSetAssignment(BoundMember Member, BoundExpression Value,
    ImmutableArray<ITypeSymbol> AdaptedTypes, IMethodSymbol Adapter, TextSpan SourceSpan) : BoundAssignment(SourceSpan)
{
    /// <summary>When requested by an adapter, its returned IDisposable belongs to the constructed view session.</summary>
    public bool OwnAdapterResult { get; init; }
    /// <summary>Optional statically bound equivalent of the complete dispatch, fallback and lifetime handling.
    /// Takes the target, evaluated object value, target descriptor and IServiceProvider, and returns void.</summary>
    public IMethodSymbol? RuntimeDispatcher { get; init; }
}
