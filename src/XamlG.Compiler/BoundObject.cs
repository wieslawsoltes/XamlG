using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundObject(INamedTypeSymbol Type, XamlElementSyntax Syntax, NamespaceScope Scope,
    string Key, string? Name, string FieldModifier, IMethodSymbol? Constructor, IMethodSymbol? FactoryMethod,
    ImmutableArray<BoundExpression> Arguments, ImmutableArray<BoundAssignment> Assignments,
    bool UsableDuringInitialization, bool SupportsInitialize, bool IsRoot, int NameScopeId);
