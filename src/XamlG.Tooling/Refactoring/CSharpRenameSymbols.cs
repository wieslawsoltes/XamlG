using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XamlG.Tooling.Refactoring;

internal sealed class CSharpRenameSymbols
{
    private readonly Dictionary<ISymbol, string> _symbols = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, string> _declarations = new(StringComparer.Ordinal);
    internal string NewName { get; }
    internal IEnumerable<ISymbol> Items => _symbols.Keys;
    private CSharpRenameSymbols(string newName) => NewName = newName;
    internal static CSharpRenameSymbols Empty(string newName) => new(newName);
    internal bool Contains(ISymbol symbol) => _symbols.ContainsKey(Normalize(symbol)) ||
        DeclarationId(symbol) is { } id && _declarations.ContainsKey(id);
    internal string MappedName(ISymbol symbol) => _symbols.TryGetValue(Normalize(symbol), out var name) ? name :
        DeclarationId(symbol) is { } id && _declarations.TryGetValue(id, out name) ? name : Name(symbol);
    internal static ISymbol Normalize(ISymbol symbol) => symbol is IMethodSymbol method
        ? (method.ReducedFrom ?? method.PartialDefinitionPart ?? method).OriginalDefinition : symbol.OriginalDefinition;
    internal static ISymbol RenameTarget(ISymbol symbol) => symbol is IMethodSymbol
        { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor } method ? method.ContainingType : Normalize(symbol);
    internal static string Name(ISymbol symbol)
    {
        var name = symbol.Name;
        return symbol is IMethodSymbol or IPropertySymbol or IEventSymbol ? name.Substring(name.LastIndexOf('.') + 1) : name;
    }
    internal static string? DeclarationId(ISymbol symbol)
    {
        symbol = Normalize(symbol); var id = symbol.GetDocumentationCommentId();
        return id == null ? null : symbol is INamespaceSymbol ? id : symbol.ContainingAssembly?.Identity + "|" + id;
    }
    internal static bool SameDeclaration(ISymbol left, ISymbol? right) => right != null &&
        (CSharpLanguageService.SameSymbol(left, right) || DeclarationId(left) is { } id && id == DeclarationId(right));

    internal static CSharpRenameSymbols Create(CSharpCompilation compilation, ISymbol selected, string newName, CancellationToken token, IEnumerable<KeyValuePair<ISymbol, string>>? linkedNames = null)
    {
        var result = new CSharpRenameSymbols(newName);
        var edges = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        if (selected is IMethodSymbol or IPropertySymbol or IEventSymbol || selected is IParameterSymbol { ContainingType.IsRecord: true } || linkedNames != null)
        {
            foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace, token))
            {
                foreach (var member in type.GetMembers())
                {
                    token.ThrowIfCancellationRequested();
                    if (CSharpLanguageService.Overridden(member) is { } parent) Connect(member, parent);
                }
                foreach (var contract in type.AllInterfaces)
                    foreach (var member in contract.GetMembers())
                    {
                        token.ThrowIfCancellationRequested();
                        if (member is IMethodSymbol { AssociatedSymbol: not null }) continue;
                        if (type.FindImplementationForInterfaceMember(member) is { } implementation) Connect(member, implementation);
                    }
                if (type.IsRecord)
                    foreach (var parameter in type.InstanceConstructors.SelectMany(method => method.Parameters))
                        foreach (var property in type.GetMembers(parameter.Name).OfType<IPropertySymbol>())
                            if (parameter.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(token) is ParameterSyntax declaration &&
                                property.DeclaringSyntaxReferences.Any(other => other.SyntaxTree == reference.SyntaxTree && other.Span == declaration.Span)))
                            {
                                Connect(parameter, property);
                                foreach (var deconstruct in type.GetMembers("Deconstruct").OfType<IMethodSymbol>().Where(method => method.IsImplicitlyDeclared))
                                    if (parameter.Ordinal < deconstruct.Parameters.Length && deconstruct.Parameters[parameter.Ordinal].Name == parameter.Name)
                                        Connect(parameter, deconstruct.Parameters[parameter.Ordinal]);
                            }
            }
        }
        var pending = new Queue<(ISymbol Symbol, string Name)>(); pending.Enqueue((Normalize(selected), newName));
        if (linkedNames != null) foreach (var linked in linkedNames) pending.Enqueue((Normalize(linked.Key), linked.Value));
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested(); var (symbol, name) = pending.Dequeue();
            if (result._symbols.TryGetValue(symbol, out var existing))
            { if (existing != name) throw new InvalidOperationException("Related declarations require conflicting new names."); continue; }
            result._symbols.Add(symbol, name);
            if (DeclarationId(symbol) is { } id) result._declarations[id] = name;
            if (edges.TryGetValue(symbol, out var linked)) foreach (var related in linked) pending.Enqueue((related, name));
        }
        return result;
        void Connect(ISymbol first, ISymbol second)
        {
            first = Normalize(first); second = Normalize(second);
            if (!edges.TryGetValue(first, out var a)) edges.Add(first, a = new(SymbolEqualityComparer.Default));
            if (!edges.TryGetValue(second, out var b)) edges.Add(second, b = new(SymbolEqualityComparer.Default));
            a.Add(second); b.Add(first);
        }
    }
    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol root, CancellationToken token)
    {
        var pending = new Stack<INamespaceOrTypeSymbol>(); pending.Push(root);
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested(); var current = pending.Pop();
            if (current is INamedTypeSymbol type) yield return type;
            foreach (var member in current.GetMembers()) if (member is INamespaceOrTypeSymbol child) pending.Push(child);
        }
    }
}
