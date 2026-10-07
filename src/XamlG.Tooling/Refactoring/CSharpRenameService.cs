using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

/// <summary>Plans source-only C# renames and verifies every bound identifier in the resulting
/// compilation. Refuses generated/XAML declarations and inheritance contracts that need a
/// framework-aware rename. No source is published on a collision, changed binding or cancellation.</summary>
public sealed class CSharpRenameService(CSharpCompilation compilation, IEnumerable<string> editablePaths)
{
    private readonly ImmutableHashSet<string> _editable = editablePaths.ToImmutableHashSet(StringComparer.Ordinal);

    public XamlRenamePlan Rename(string path, int offset, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (newName == null) throw new ArgumentNullException(nameof(newName));
        if (newName.StartsWith("@", StringComparison.Ordinal)) newName = newName.Substring(1);
        var escaped = CSharpLanguageService.Escape(newName);
        var identifier = SyntaxFactory.ParseToken(escaped);
        if (newName.Length is 0 or > 512 || !identifier.IsKind(SyntaxKind.IdentifierToken) || identifier.ContainsDiagnostics ||
            identifier.Text != escaped || identifier.ValueText != newName) throw new ArgumentException("Use a valid C# identifier.", nameof(newName));
        if (!_editable.Contains(path)) throw new InvalidOperationException("Generated source is read-only; rename its XAML declaration instead.");
        var service = new CSharpLanguageService(compilation, _editable);
        var selected = service.ResolveSymbol(path, offset, cancellationToken) ?? throw new InvalidOperationException("Select a resolved C# identifier.");
        if (selected is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor } constructor) selected = constructor.ContainingType;
        if (selected.Kind is not (SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Field or SymbolKind.Property or SymbolKind.Event or SymbolKind.Local or SymbolKind.Parameter or SymbolKind.TypeParameter) ||
            selected.IsImplicitlyDeclared || selected.Locations.All(l => !l.IsInSource) ||
            selected.Locations.Any(l => l.SourceTree == null || !_editable.Contains(l.SourceTree.FilePath)))
            throw new InvalidOperationException("This symbol has metadata, generated or unsupported declarations. Rename its owning source declaration instead.");
        if (selected.IsVirtual || selected.IsOverride || selected.IsAbstract || selected.ContainingType?.TypeKind == TypeKind.Interface ||
            selected is IMethodSymbol { ExplicitInterfaceImplementations.IsEmpty: false } ||
            selected is IPropertySymbol { ExplicitInterfaceImplementations.IsEmpty: false } || selected is IEventSymbol { ExplicitInterfaceImplementations.IsEmpty: false } ||
            selected.ContainingType?.AllInterfaces.SelectMany(i => i.GetMembers()).Any(member =>
                CSharpLanguageService.SameSymbol(selected.ContainingType.FindImplementationForInterfaceMember(member), selected)) == true)
            throw new InvalidOperationException("Renaming an inheritance or interface contract requires a workspace-wide contract rename.");
        var trigger = service.GetSymbol(path, offset, cancellationToken)!.Span;
        if (selected.Name == newName) return new(selected.Name, newName, trigger, ImmutableArray<XamlDocumentEdits>.Empty);
        if (compilation.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Resolve compilation errors before semantic C# rename.");

        var edits = ImmutableArray.CreateBuilder<XamlDocumentEdits>();
        var observations = new List<(string Path, int Start, ISymbol Symbol)>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree); var changes = ImmutableArray.CreateBuilder<XamlTextChange>();
            foreach (var token in tree.GetRoot(cancellationToken).DescendantTokens(descendIntoTrivia: true).Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var symbol = CSharpLanguageService.SymbolAt(model, token, cancellationToken);
                if (symbol == null) continue;
                // Constructor/destructor declaration identifiers follow a renamed type.
                var target = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor } method && selected is INamedTypeSymbol
                    ? method.ContainingType : symbol;
                if (_editable.Contains(tree.FilePath)) observations.Add((tree.FilePath, token.SpanStart, symbol));
                if (!CSharpLanguageService.SameSymbol(target, selected) || token.ValueText != selected.Name) continue;
                if (!_editable.Contains(tree.FilePath)) throw new InvalidOperationException("Generated code references this symbol. Rename the owning XAML declaration or update the XAML/C# project together.");
                changes.Add(new(new(token.SpanStart, token.Span.Length), escaped));
            }
            if (changes.Count != 0) edits.Add(new(tree.FilePath, tree.GetText(cancellationToken).ToString(), null, changes.ToImmutable()));
        }
        var planned = edits.ToImmutable();
        if (planned.IsEmpty) throw new InvalidOperationException("No editable C# declaration was found.");
        var updated = compilation;
        foreach (var edit in planned)
        {
            var previous = service.Tree(edit.Path);
            var text = previous.GetText(cancellationToken).WithChanges(edit.Changes.Select(c =>
                new Microsoft.CodeAnalysis.Text.TextChange(new(c.Span.Start, c.Span.Length), c.NewText)));
            updated = updated.ReplaceSyntaxTree(previous, previous.WithChangedText(text));
        }
        if (updated.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("The new name creates compiler errors. No source was changed.");
        var next = new CSharpLanguageService(updated, _editable);
        var mapped = new Dictionary<ISymbol, ISymbol?>(SymbolEqualityComparer.Default);
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mapped.TryGetValue(observation.Symbol, out var expected))
            {
                var declaration = observation.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
                expected = declaration?.SourceTree is { } declarationTree ?
                    next.ResolveSymbol(declarationTree.FilePath, Map(declarationTree.FilePath, declaration.SourceSpan.Start), cancellationToken) : observation.Symbol;
                mapped.Add(observation.Symbol, expected);
            }
            var actual = next.ResolveSymbol(observation.Path, Map(observation.Path, observation.Start), cancellationToken);
            if (!CSharpLanguageService.SameSymbol(expected, actual))
                throw new InvalidOperationException("The new name changes another identifier's binding or an implicit declaration. No source was changed.");
        }
        return new(selected.Name, newName, trigger, planned);

        int Map(string source, int position)
        {
            var edit = planned.FirstOrDefault(e => e.Path == source);
            return position + (edit?.Changes.Where(c => c.Span.End <= position).Sum(c => c.NewText.Length - c.Span.Length) ?? 0);
        }
    }
}
