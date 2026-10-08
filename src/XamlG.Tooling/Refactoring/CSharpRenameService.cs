using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

/// <summary>Plans atomic C# source renames, including source inheritance/interface contracts.
/// Every bound source identifier is checked in the candidate compilation before a plan is returned.
/// Framework hosts can regenerate their sources through the coordinated project rename service.</summary>
public sealed class CSharpRenameService(CSharpCompilation compilation, IEnumerable<string> editablePaths)
{
    private readonly ImmutableHashSet<string> _editable = editablePaths.ToImmutableHashSet(StringComparer.Ordinal);

    public XamlRenamePlan Rename(string path, int offset, string newName, CancellationToken cancellationToken = default)
    {
        var pending = Prepare(path, offset, newName, false, cancellationToken);
        pending.Verify(pending.Apply(compilation, cancellationToken), cancellationToken);
        return pending.Plan;
    }

    internal CSharpRenamePending Prepare(string path, int offset, string newName, bool regenerate, CancellationToken token)
    {
        if (!regenerate && !_editable.Contains(path)) throw new InvalidOperationException("Generated source is read-only; rename its owning source declaration instead.");
        var service = new CSharpLanguageService(compilation, _editable);
        var symbol = service.ResolveSymbol(path, offset, token) ?? throw new InvalidOperationException("Select a resolved C# identifier.");
        return Prepare(symbol, service.GetSymbol(path, offset, token)!.Span, newName, regenerate, token);
    }

    internal CSharpRenamePending Prepare(ISymbol selected, TextSpan trigger, string newName, bool regenerate, CancellationToken token, IEnumerable<KeyValuePair<ISymbol, string>>? linkedNames = null)
    {
        token.ThrowIfCancellationRequested();
        newName = ValidateName(newName);
        selected = CSharpRenameSymbols.RenameTarget(selected);
        var targets = CSharpRenameSymbols.Create(compilation, selected, newName, token, linkedNames);
        foreach (var target in targets.Items)
        {
            var recordParameter = target is IParameterSymbol { ContainingSymbol: IMethodSymbol { IsImplicitlyDeclared: true, Name: "Deconstruct" }, ContainingType.IsRecord: true } &&
                target.ContainingType.Locations.Any(location => location.SourceTree != null && _editable.Contains(location.SourceTree.FilePath));
            if (target.Kind is not (SymbolKind.NamedType or SymbolKind.Namespace or SymbolKind.Alias or SymbolKind.Method or SymbolKind.Field or
                SymbolKind.Property or SymbolKind.Event or SymbolKind.Local or SymbolKind.Parameter or SymbolKind.TypeParameter or SymbolKind.RangeVariable or SymbolKind.Label) ||
                !recordParameter && (!target.Locations.Any(l => l.SourceTree != null && _editable.Contains(l.SourceTree.FilePath)) ||
                !regenerate && target.Locations.Any(l => l.SourceTree == null || !_editable.Contains(l.SourceTree.FilePath))))
                throw new InvalidOperationException("The rename includes metadata or a declaration without editable source. Its contract cannot be renamed in this project.");
            if (target is INamespaceSymbol ns && ns.ConstituentNamespaces.Any(part => part.ContainingAssembly != null &&
                !SymbolEqualityComparer.Default.Equals(part.ContainingAssembly, compilation.Assembly)))
                throw new InvalidOperationException("This namespace also belongs to a referenced assembly and cannot be renamed as one source namespace.");
        }
        if (compilation.GetDiagnostics(token).Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Resolve compilation errors before semantic C# rename.");
        var edits = ImmutableArray.CreateBuilder<XamlDocumentEdits>();
        var observations = ImmutableArray.CreateBuilder<CSharpRenameObservation>();
        var oldName = CSharpRenameSymbols.Name(selected);
        foreach (var tree in compilation.SyntaxTrees)
        {
            token.ThrowIfCancellationRequested();
            var editable = _editable.Contains(tree.FilePath); var model = compilation.GetSemanticModel(tree);
            var changes = ImmutableArray.CreateBuilder<XamlTextChange>();
            foreach (var identifierToken in tree.GetRoot(token).DescendantTokens(descendIntoTrivia: true).Where(t => t.IsKind(SyntaxKind.IdentifierToken) && !t.IsMissing))
            {
                token.ThrowIfCancellationRequested();
                var symbol = CSharpLanguageService.SymbolAt(model, identifierToken, token);
                if (symbol == null) continue;
                if (editable) observations.Add(new(tree.FilePath, identifierToken.SpanStart, symbol));
                var target = CSharpRenameSymbols.RenameTarget(symbol);
                if (!targets.Contains(target) || identifierToken.ValueText != CSharpRenameSymbols.Name(target) || CSharpRenameSymbols.Name(target) == targets.MappedName(target)) continue;
                if (!editable)
                {
                    if (!regenerate) throw new InvalidOperationException("Generated code references this symbol. Use a coordinated XAML/C# project rename.");
                    continue;
                }
                changes.Add(new(new(identifierToken.SpanStart, identifierToken.Span.Length), CSharpLanguageService.Escape(targets.MappedName(target))));
            }
            if (changes.Count != 0) edits.Add(new(tree.FilePath, tree.GetText(token).ToString(), null, changes.ToImmutable()));
        }
        if (edits.Count == 0 && oldName != newName) throw new InvalidOperationException("No editable C# declaration was found.");
        return new(compilation, _editable, targets, new(oldName, newName, trigger, edits.ToImmutable()), observations.ToImmutable());
    }
    internal static string ValidateName(string newName)
    {
        if (newName == null) throw new ArgumentNullException(nameof(newName));
        if (newName.StartsWith("@", StringComparison.Ordinal)) newName = newName.Substring(1);
        var escaped = CSharpLanguageService.Escape(newName); var identifier = SyntaxFactory.ParseToken(escaped);
        if (newName.Length is 0 or > 512 || !identifier.IsKind(SyntaxKind.IdentifierToken) || identifier.ContainsDiagnostics ||
            identifier.Text != escaped || identifier.ValueText != newName) throw new ArgumentException("Use a valid C# identifier.", nameof(newName));
        return newName;
    }
}

internal sealed record CSharpRenameObservation(string Path, int Start, ISymbol Symbol);
internal sealed class CSharpRenamePending(CSharpCompilation original, ImmutableHashSet<string> editable,
    CSharpRenameSymbols targets, XamlRenamePlan plan, ImmutableArray<CSharpRenameObservation> observations)
{
    internal XamlRenamePlan Plan { get; } = plan;
    internal CSharpRenameSymbols Targets { get; } = targets;
    internal CSharpRenamePending WithChanges(ImmutableArray<XamlDocumentEdits> additions)
    {
        var documents = Plan.Documents.Concat(additions).GroupBy(document => document.Path, StringComparer.Ordinal).Select(group =>
        {
            var first = group.First();
            if (group.Any(document => document.OriginalText != first.OriginalText)) throw new InvalidOperationException("Related rename edits have different source snapshots.");
            return first with { Changes = group.SelectMany(document => document.Changes).Distinct().OrderBy(change => change.Span.Start).ToImmutableArray() };
        }).ToImmutableArray();
        _ = new CSharpRenamePositions(documents);
        return new(original, editable, Targets, Plan with { Documents = documents }, observations);
    }
    internal CSharpCompilation Apply(CSharpCompilation source, CancellationToken token)
    {
        foreach (var edit in Plan.Documents)
        {
            token.ThrowIfCancellationRequested();
            var previous = source.SyntaxTrees.Single(tree => tree.FilePath == edit.Path);
            if (previous.GetText(token).ToString() != edit.OriginalText) throw new InvalidOperationException("The C# source snapshot changed while planning rename.");
            source = source.ReplaceSyntaxTree(previous, previous.WithChangedText(previous.GetText(token).WithChanges(edit.Changes.Select(change =>
                new Microsoft.CodeAnalysis.Text.TextChange(new(change.Span.Start, change.Span.Length), change.NewText)))));
        }
        return source;
    }
    internal CSharpRenameVerification Verify(CSharpCompilation candidate, CancellationToken token)
    {
        if (candidate.GetDiagnostics(token).Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("The new name creates compiler errors. No source was changed.");
        var verifier = new CSharpRenameVerification(original, candidate, editable, Targets, Plan.Documents, token);
        var service = new CSharpLanguageService(candidate, editable);
        foreach (var observation in observations)
        {
            token.ThrowIfCancellationRequested();
            var actual = service.ResolveSymbol(observation.Path, verifier.Position(observation.Path, observation.Start), token);
            if (!verifier.Matches(observation.Symbol, actual))
                throw new InvalidOperationException("The new name changes another identifier's binding or an implicit declaration. No source was changed.");
        }
        return verifier;
    }
}
