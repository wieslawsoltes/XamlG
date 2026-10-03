using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

public sealed record BoundPropertyAccessExpression(BoundExpression Receiver, IPropertySymbol Property,
    ImmutableArray<BoundExpression> IndexArguments, TextSpan SourceSpan)
    : BoundExpression(Property.Type, SourceSpan);
