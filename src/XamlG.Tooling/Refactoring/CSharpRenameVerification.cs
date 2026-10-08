using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

// Maps declaration identities, never generated offsets: regeneration can rename or reorder
// generated trees. Source declarations retain anchors through the exact planned text edits.
internal sealed class CSharpRenameVerification(CSharpCompilation original, CSharpCompilation candidate,
    ImmutableHashSet<string> editable, CSharpRenameSymbols targets, ImmutableArray<XamlDocumentEdits> documents, CancellationToken token)
{
    private readonly CSharpLanguageService _next = new(candidate, editable);
    private readonly Dictionary<ISymbol, ISymbol?> _mapped = new(SymbolEqualityComparer.Default);
    private readonly CSharpRenamePositions _positions = new(documents);
    internal int Position(string path, int position) => _positions.Position(path, position);
    internal bool Matches(ISymbol previous, ISymbol? actual) => previous is ITypeSymbol before && actual is ITypeSymbol after
        ? Type(before, after) : MatchesDeclaration(previous, actual);
    private bool MatchesDeclaration(ISymbol previous, ISymbol? actual) => Map(previous) is { } expected && CSharpRenameSymbols.SameDeclaration(expected, actual);
    internal ISymbol? Map(ISymbol symbol)
    {
        token.ThrowIfCancellationRequested(); symbol = CSharpRenameSymbols.Normalize(symbol);
        if (_mapped.TryGetValue(symbol, out var cached)) return cached;
        _mapped.Add(symbol, null);
        var result = MapCore(symbol); _mapped[symbol] = result; return result;
    }
    private ISymbol? MapCore(ISymbol symbol)
    {
        if (symbol is IAssemblySymbol assembly) return assembly.Identity.Equals(original.Assembly.Identity) ? candidate.Assembly : assembly;
        if (symbol is IMethodSymbol { AssociatedSymbol: { } associated } accessor)
            return Map(associated) switch
            {
                IPropertySymbol property => accessor.MethodKind == MethodKind.PropertyGet ? property.GetMethod : property.SetMethod,
                IEventSymbol ev => accessor.MethodKind switch { MethodKind.EventAdd => ev.AddMethod, MethodKind.EventRemove => ev.RemoveMethod, MethodKind.EventRaise => ev.RaiseMethod, _ => null },
                _ => null
            };
        if (symbol is INamespaceSymbol ns)
        {
            if (ns.IsGlobalNamespace) return candidate.GlobalNamespace;
            return (Map(ns.ContainingNamespace) as INamespaceSymbol)?.GetNamespaceMembers().SingleOrDefault(item => item.Name == targets.MappedName(ns));
        }
        foreach (var location in symbol.Locations.Where(location => location.SourceTree != null && editable.Contains(location.SourceTree.FilePath)))
        {
            var tree = original.SyntaxTrees.SingleOrDefault(item => item.FilePath == location.SourceTree!.FilePath);
            if (tree == null) continue;
            var model = original.GetSemanticModel(tree);
            foreach (var identifier in tree.GetRoot(token).DescendantTokens(location.SourceSpan, descendIntoTrivia: true).Where(item => item.IsKind(SyntaxKind.IdentifierToken)))
            {
                var bound = CSharpLanguageService.SymbolAt(model, identifier, token);
                if (!CSharpRenameSymbols.SameDeclaration(symbol, bound)) continue;
                var next = _next.ResolveSymbol(tree.FilePath, Position(tree.FilePath, identifier.SpanStart), token);
                if (next?.Kind == symbol.Kind) return CSharpRenameSymbols.Normalize(next);
            }
        }
        if (symbol is IParameterSymbol parameter)
            return Map(parameter.ContainingSymbol) switch
            {
                IMethodSymbol method when parameter.Ordinal < method.Parameters.Length => method.Parameters[parameter.Ordinal],
                IPropertySymbol property when parameter.Ordinal < property.Parameters.Length => property.Parameters[parameter.Ordinal], _ => null
            };
        if (symbol is ITypeParameterSymbol typeParameter)
            return Map(typeParameter.ContainingSymbol) switch
            {
                IMethodSymbol method when typeParameter.Ordinal < method.TypeParameters.Length => method.TypeParameters[typeParameter.Ordinal],
                INamedTypeSymbol type when typeParameter.Ordinal < type.TypeParameters.Length => type.TypeParameters[typeParameter.Ordinal], _ => null
            };
        // External definitions retain their assembly identity. Constructed member identities
        // are compared by their original definitions, as in Roslyn's reference navigation.
        // XAML binds against the source compilation before generated trees are added.
        // Its implicit members have no editable declaration token, but still belong to
        // this source assembly even though their Roslyn assembly instance is different.
        if (symbol.ContainingAssembly != null && !symbol.ContainingAssembly.Identity.Equals(original.Assembly.Identity)) return symbol;
        if (symbol is INamedTypeSymbol named)
        {
            var parent = named.ContainingType == null ? Map(named.ContainingNamespace) : Map(named.ContainingType);
            return (parent as INamespaceOrTypeSymbol)?.GetTypeMembers(targets.MappedName(named), named.Arity).SingleOrDefault();
        }
        if (symbol.ContainingType is { } owner && Map(owner) is INamedTypeSymbol nextOwner)
        {
            var matches = nextOwner.GetMembers().Where(item => item.Kind == symbol.Kind &&
                (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor }
                    ? item is IMethodSymbol method && method.MethodKind == ((IMethodSymbol)symbol).MethodKind
                    : CSharpRenameSymbols.Name(item) == targets.MappedName(symbol)))
                .Where(item => Signature(symbol, item)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        return null;
    }
    private bool Signature(ISymbol previous, ISymbol next)
    {
        if (previous.IsStatic != next.IsStatic) return false;
        if (previous is IMethodSymbol method && next is IMethodSymbol changed)
            return method.MethodKind == changed.MethodKind && method.Arity == changed.Arity && Parameters(method.Parameters, changed.Parameters) &&
                Contracts(method.ExplicitInterfaceImplementations, changed.ExplicitInterfaceImplementations);
        if (previous is IPropertySymbol property && next is IPropertySymbol nextProperty)
            return property.IsIndexer == nextProperty.IsIndexer && Parameters(property.Parameters, nextProperty.Parameters) &&
                Contracts(property.ExplicitInterfaceImplementations, nextProperty.ExplicitInterfaceImplementations);
        if (previous is IEventSymbol ev && next is IEventSymbol nextEvent)
            return Contracts(ev.ExplicitInterfaceImplementations, nextEvent.ExplicitInterfaceImplementations);
        return true;
    }
    private bool Contracts<T>(ImmutableArray<T> before, ImmutableArray<T> after) where T : ISymbol =>
        before.Length == after.Length && before.All(contract => after.Any(next => Matches(contract, next)));
    private bool Parameters(ImmutableArray<IParameterSymbol> before, ImmutableArray<IParameterSymbol> after) => before.Length == after.Length &&
        before.Zip(after, (left, right) => left.RefKind == right.RefKind && Type(left.Type, right.Type)).All(equal => equal);
    private bool Type(ITypeSymbol before, ITypeSymbol after)
    {
        if (before.TypeKind != after.TypeKind || before.NullableAnnotation != after.NullableAnnotation) return false;
        if (before is ITypeParameterSymbol leftParameter && after is ITypeParameterSymbol rightParameter)
            return leftParameter.TypeParameterKind == rightParameter.TypeParameterKind && leftParameter.Ordinal == rightParameter.Ordinal &&
                MatchesDeclaration(leftParameter.ContainingSymbol, rightParameter.ContainingSymbol);
        if (before is IArrayTypeSymbol leftArray && after is IArrayTypeSymbol rightArray) return leftArray.Rank == rightArray.Rank && Type(leftArray.ElementType, rightArray.ElementType);
        if (before is IPointerTypeSymbol leftPointer && after is IPointerTypeSymbol rightPointer) return Type(leftPointer.PointedAtType, rightPointer.PointedAtType);
        if (before is INamedTypeSymbol left && after is INamedTypeSymbol right)
            return MatchesDeclaration(left.OriginalDefinition, right.OriginalDefinition) && left.TypeArguments.Length == right.TypeArguments.Length &&
                left.TypeArguments.Zip(right.TypeArguments, Type).All(equal => equal);
        return CSharpLanguageService.SameSymbol(before, after);
    }
}
