using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Creates a value, initializes it through an IDisposable scope, then returns it or one of its properties.
/// The scope factory is a parameterless instance method or a constructor taking the owner.</summary>
public sealed record BoundScopedInitializationExpression(BoundExpression Creation, IMethodSymbol ScopeFactory,
    ImmutableArray<BoundBuilderCall> Calls, IPropertySymbol? ResultProperty, TextSpan SourceSpan)
    : BoundExpression(ResultProperty?.Type ?? Creation.Type, SourceSpan);
