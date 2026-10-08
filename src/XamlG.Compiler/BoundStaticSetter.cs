using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace XamlG.Compiler;

/// <summary>A typed static setter receiving the target, descriptor fields and member value.</summary>
public sealed record BoundStaticSetter(IMethodSymbol Method, ImmutableArray<BoundStaticExpression> Descriptors)
{
    public BoundStaticSetter(IMethodSymbol method, ImmutableArray<IFieldSymbol> fields)
        : this(method, fields.Select(field => new BoundStaticExpression(field, field.Type, default)).ToImmutableArray()) { }
}
