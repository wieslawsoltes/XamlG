using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XamlG.Tooling;

public sealed record CSharpNavigationSymbol(int Id, int? ParentId, string Name, string Kind, string Display,
    string? Type, string Accessibility, bool IsStatic, bool IsImplicit, string? Container,
    ImmutableArray<CSharpLocation> Locations, CSharpLocation? Extent = null);
public sealed record CSharpSymbolResult(ImmutableArray<CSharpNavigationSymbol> Symbols, bool Truncated);
public sealed record CSharpTypeHierarchy(CSharpNavigationSymbol Type, ImmutableArray<CSharpNavigationSymbol> BaseTypes,
    ImmutableArray<CSharpNavigationSymbol> Interfaces, ImmutableArray<CSharpNavigationSymbol> DerivedTypes, bool Truncated);

public sealed partial class CSharpLanguageService
{
    public CSharpSymbolResult GetDocumentSymbols(string path, bool includeLocals = false, int maximumResults = 1000,
        int maximumDepth = 16, CancellationToken cancellationToken = default)
    {
        Bound(maximumResults); DepthBound(maximumDepth);
        var tree = Tree(path); var model = _compilation.GetSemanticModel(tree); var text = new InspectionText();
        var budget = new NavigationBudget(); var result = ImmutableArray.CreateBuilder<CSharpNavigationSymbol>();
        var ancestors = new Dictionary<SyntaxNode, (int Id, int Depth)>(); var truncated = false;
        foreach (var node in Declarations(tree, includeLocals, budget, cancellationToken))
        {
            var symbol = model.GetDeclaredSymbol(node, cancellationToken); if (symbol == null) continue;
            var parent = node.Ancestors().FirstOrDefault(ancestors.ContainsKey);
            var depth = parent == null ? 0 : ancestors[parent].Depth + 1;
            if (depth > maximumDepth) { truncated = true; continue; }
            if (result.Count == maximumResults) { truncated = true; break; }
            var id = result.Count;
            result.Add(NavigationSymbol(symbol, id, text, cancellationToken, parent == null ? null : ancestors[parent].Id,
                Location(tree, node.Span, true, cancellationToken)));
            ancestors.Add(node, (id, depth));
        }
        return new(result.ToImmutable(), truncated || budget.Truncated || text.Truncated);
    }

    public CSharpSymbolResult FindSymbols(string query = "", bool includeGenerated = false, int maximumResults = 200,
        CancellationToken cancellationToken = default)
    {
        Bound(maximumResults);
        if (query == null || query.Length > 512) throw new ArgumentException("Use a symbol query of at most 512 characters.", nameof(query));
        var result = ImmutableArray.CreateBuilder<CSharpNavigationSymbol>(); var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var budget = new NavigationBudget(); var text = new InspectionText(); var truncated = false;
        foreach (var tree in _compilation.SyntaxTrees.Where(tree => includeGenerated || _editable.Contains(tree.FilePath)).OrderBy(tree => tree.FilePath, StringComparer.Ordinal))
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var node in Declarations(tree, false, budget, cancellationToken))
            {
                var symbol = model.GetDeclaredSymbol(node, cancellationToken); if (symbol == null || !seen.Add(symbol)) continue;
                if (symbol.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && symbol.ToDisplayString().IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (result.Count == maximumResults) { truncated = true; break; }
                result.Add(NavigationSymbol(symbol, result.Count, text, cancellationToken));
            }
            if (truncated || budget.Truncated) break;
        }
        return new(result.ToImmutable(), truncated || budget.Truncated || text.Truncated);
    }

    public CSharpSymbolResult GetMembers(string path, int offset, bool includeInherited = true, bool includeImplicit = false,
        int maximumResults = 200, CancellationToken cancellationToken = default)
    {
        Bound(maximumResults); var symbol = ResolveSymbol(path, offset, cancellationToken);
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        INamespaceOrTypeSymbol? container = symbol as INamespaceOrTypeSymbol ?? SelectedType(path, offset, symbol, cancellationToken) as INamespaceOrTypeSymbol;
        if (container == null) return new(ImmutableArray<CSharpNavigationSymbol>.Empty, false);
        var result = ImmutableArray.CreateBuilder<CSharpNavigationSymbol>(); var text = new InspectionText(); var truncated = false;
        var containers = new Queue<INamespaceOrTypeSymbol>(); containers.Enqueue(container);
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        while (containers.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested(); var current = containers.Dequeue();
            if (!seen.Add(current)) continue;
            foreach (var member in current.GetMembers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!includeImplicit && (member.IsImplicitlyDeclared || member is IMethodSymbol { AssociatedSymbol: not null })) continue;
                if (result.Count == maximumResults) { truncated = true; break; }
                result.Add(NavigationSymbol(member, result.Count, text, cancellationToken));
            }
            if (truncated) break;
            if (includeInherited && current is INamedTypeSymbol type)
            {
                if (type.BaseType != null) containers.Enqueue(type.BaseType);
                if (type.TypeKind == TypeKind.Interface) foreach (var inherited in type.Interfaces) containers.Enqueue(inherited);
            }
        }
        return new(result.ToImmutable(), truncated || text.Truncated);
    }

    public ImmutableArray<CSharpLocation> GetTypeDefinitions(string path, int offset, CancellationToken cancellationToken = default)
    {
        var type = SelectedType(path, offset, ResolveSymbol(path, offset, cancellationToken), cancellationToken);
        return type == null ? ImmutableArray<CSharpLocation>.Empty : Locations(type.OriginalDefinition, cancellationToken);
    }

    public CSharpTypeHierarchy? GetTypeHierarchy(string path, int offset, int maximumResults = 200, CancellationToken cancellationToken = default)
    {
        Bound(maximumResults);
        var type = SelectedType(path, offset, ResolveSymbol(path, offset, cancellationToken), cancellationToken) as INamedTypeSymbol;
        if (type == null) return null;
        var text = new InspectionText(); var total = 0; var truncated = false; var budget = new NavigationBudget();
        var bases = ImmutableArray.CreateBuilder<CSharpNavigationSymbol>(); var interfaces = ImmutableArray.CreateBuilder<CSharpNavigationSymbol>();
        var derived = ImmutableArray.CreateBuilder<CSharpNavigationSymbol>();
        var root = NavigationSymbol(type, total++, text, cancellationToken);
        for (var current = type.BaseType; current != null; current = current.BaseType) Add(bases, current);
        foreach (var contract in type.AllInterfaces) Add(interfaces, contract);
        foreach (var candidate in SourceTypes(budget, cancellationToken))
        {
            if (!SameSymbol(type, candidate) && DerivesFrom(candidate, type)) Add(derived, candidate);
            if (truncated) break;
        }
        return new(root, bases.ToImmutable(), interfaces.ToImmutable(), derived.ToImmutable(), truncated || text.Truncated || budget.Truncated);
        void Add(ImmutableArray<CSharpNavigationSymbol>.Builder items, INamedTypeSymbol value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total >= maximumResults) { truncated = true; return; }
            items.Add(NavigationSymbol(value, total++, text, cancellationToken));
        }
    }

    public CSharpReferenceResult GetImplementations(string path, int offset, int maximumResults = 1000, CancellationToken cancellationToken = default)
    {
        Bound(maximumResults); var symbol = ResolveSymbol(path, offset, cancellationToken);
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol == null) return new(ImmutableArray<CSharpLocation>.Empty, false);
        var result = new HashSet<CSharpLocation>(); var truncated = false; var budget = new NavigationBudget();
        foreach (var type in SourceTypes(budget, cancellationToken))
        {
            if (symbol is INamedTypeSymbol contract)
            { if (!SameSymbol(type, contract) && DerivesFrom(type, contract)) Add(type); }
            else if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
            {
                foreach (var implemented in type.AllInterfaces.Where(item => SameSymbol(item, symbol.ContainingType)))
                    foreach (var member in implemented.GetMembers().Where(item => SameSymbol(item, symbol)))
                        if (type.FindImplementationForInterfaceMember(member) is { } implementation) Add(implementation);
            }
            else
                foreach (var member in type.GetMembers())
                    for (var overridden = Overridden(member); overridden != null; overridden = Overridden(overridden))
                        if (SameSymbol(overridden, symbol)) { Add(member); break; }
            if (truncated) break;
        }
        return new(result.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Start).ToImmutableArray(), truncated || budget.Truncated);
        void Add(ISymbol item)
        {
            foreach (var location in Locations(item, cancellationToken))
            { if (result.Contains(location)) continue; if (result.Count == maximumResults) { truncated = true; return; } result.Add(location); }
        }
    }

    internal static ISymbol? Overridden(ISymbol symbol) => symbol switch
    { IMethodSymbol method => method.OverriddenMethod, IPropertySymbol property => property.OverriddenProperty, IEventSymbol ev => ev.OverriddenEvent, _ => null };
    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol target)
    {
        if (type.AllInterfaces.Any(contract => SameSymbol(contract, target))) return true;
        for (var current = type.BaseType; current != null; current = current.BaseType) if (SameSymbol(current, target)) return true;
        return false;
    }
    private ITypeSymbol? SelectedType(string path, int offset, ISymbol? symbol, CancellationToken token)
    {
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor }) return symbol.ContainingType;
        return SymbolType(symbol) ?? (symbol is IMethodSymbol method ? method.ContainingType : null) ??
            (Token(Tree(path), offset, token).Parent is { } node ? _compilation.GetSemanticModel(Tree(path)).GetTypeInfo(node, token).Type : null);
    }
    private CSharpNavigationSymbol NavigationSymbol(ISymbol symbol, int id, InspectionText text, CancellationToken token,
        int? parent = null, CSharpLocation? extent = null)
    {
        var kind = symbol switch { INamedTypeSymbol type => type.TypeKind.ToString(), IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "Constructor", _ => symbol.Kind.ToString() };
        var name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ? symbol.ContainingType.Name : symbol.Name;
        return new(id, parent, text.Get(name, 512)!, kind, text.Get(symbol.ToDisplayString(), 4096)!, text.Get(SymbolType(symbol)?.ToDisplayString()),
            symbol.DeclaredAccessibility.ToString(), symbol.IsStatic, symbol.IsImplicitlyDeclared, text.Get(symbol.ContainingSymbol?.ToDisplayString()), Locations(symbol, token), extent);
    }
    private IEnumerable<INamedTypeSymbol> SourceTypes(NavigationBudget budget, CancellationToken token)
    {
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var declaration in Declarations(tree, false, budget, token))
                if (declaration is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax && model.GetDeclaredSymbol(declaration, token) is INamedTypeSymbol type && seen.Add(type)) yield return type;
            if (budget.Truncated) yield break;
        }
    }
    private sealed class NavigationBudget { public int Remaining = 100000; public bool Truncated; }
    private static IEnumerable<SyntaxNode> Declarations(SyntaxTree tree, bool locals, NavigationBudget budget, CancellationToken token)
    {
        var pending = new Stack<SyntaxNode>(); pending.Push(tree.GetRoot(token));
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            if (--budget.Remaining < 0) { budget.Truncated = true; yield break; }
            var node = pending.Pop();
            if (node is BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax or
                BasePropertyDeclarationSyntax or EnumMemberDeclarationSyntax || node is VariableDeclaratorSyntax variable && (locals || variable.Parent?.Parent is BaseFieldDeclarationSyntax) ||
                locals && node is LocalFunctionStatementSyntax or ParameterSyntax or TypeParameterSyntax or SingleVariableDesignationSyntax or CatchDeclarationSyntax or ForEachStatementSyntax)
                yield return node;
            if (!locals && node is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or VariableDeclaratorSyntax or DelegateDeclarationSyntax) continue;
            foreach (var child in node.ChildNodes().Reverse()) pending.Push(child);
        }
    }
}
