using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Constructs a typed builder once, invokes its methods in order, then obtains its result.</summary>
public sealed record BoundBuilderExpression(BoundExpression Creation, ImmutableArray<BoundBuilderCall> Calls,
    IMethodSymbol ResultMethod, TextSpan SourceSpan) : BoundExpression(ResultMethod.ReturnType, SourceSpan);

/// <summary>An instance call on the builder, with arguments evaluated immediately before that call.</summary>
public sealed record BoundBuilderCall(IMethodSymbol Method, ImmutableArray<BoundExpression> Arguments);
