using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace XamlG.Compiler;

/// <summary>A typed static setter receiving the target, descriptor fields and member value.</summary>
public sealed record BoundStaticSetter(IMethodSymbol Method, ImmutableArray<IFieldSymbol> Descriptors);
