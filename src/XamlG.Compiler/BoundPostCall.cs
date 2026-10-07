using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace XamlG.Compiler;

/// <summary>A static call after a successful assignment. Receives the same target,
/// selected already-evaluated arguments, then the additional bound values.</summary>
public sealed record BoundPostCall(IMethodSymbol Method, ImmutableArray<int> ArgumentIndices, ImmutableArray<BoundExpression> Arguments);
