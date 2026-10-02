using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;

/// <summary>Mutation is confined to one binding operation; the published bound tree is immutable.</summary>
public sealed class ObjectBindingBuilder
{
    public ObjectBindingBuilder(INamedTypeSymbol type, XamlElementSyntax syntax, NamespaceScope scope, string key, bool isRoot, int nameScopeId)
    { Type = type; Syntax = syntax; Scope = scope; Key = key; IsRoot = isRoot; NameScopeId = nameScopeId; }
    public INamedTypeSymbol Type { get; }
    public XamlElementSyntax Syntax { get; }
    public NamespaceScope Scope { get; }
    public string Key { get; set; }
    public bool IsRoot { get; }
    public int NameScopeId { get; }
    public string? Name { get; set; }
    public string FieldModifier { get; set; } = "internal";
    public IMethodSymbol? Constructor { get; set; }
    public IMethodSymbol? FactoryMethod { get; set; }
    public ImmutableArray<BoundExpression> Arguments { get; set; } = ImmutableArray<BoundExpression>.Empty;
    public List<BoundAssignment> Assignments { get; } = new();
    public HashSet<string> AssignedScalars { get; } = new(StringComparer.Ordinal);
    public XamlAnnotationStore Annotations { get; } = new();
    public BoundObject Build(RoslynTypeSystem symbols) => new(Type, Syntax, Scope, Key, Name, FieldModifier, Constructor, FactoryMethod,
        Arguments, Assignments.ToImmutableArray(), symbols.IsUsableDuringInitialization(Type),
        Type.AllInterfaces.Any(i => i.HasMetadataName(ClrNames.ISupportInitialize)), IsRoot, NameScopeId);
}
