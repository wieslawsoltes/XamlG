using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

public sealed record XamlProjectRenameTarget(string Name, TextSpan Span, string Kind);

/// <summary>Renames resolved C#/XAML symbols in one immutable project snapshot. Regenerates
/// compiler-owned sources and verifies C# and XAML bindings before returning editable source changes.</summary>
public sealed class XamlProjectRenameService
{
    private readonly XamlCompilationSession _compiler;
    private readonly XamlProjectCompilation _project;
    private readonly CSharpCompilation _compilation;
    private readonly ImmutableHashSet<string> _editable;
    private readonly ImmutableArray<XamlAnalysis> _analyses;
    public XamlProjectRenameService(XamlCompilationSession compiler, XamlProjectCompilation project,
        CSharpCompilation compilation, IEnumerable<string> editablePaths)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _editable = editablePaths.ToImmutableHashSet(StringComparer.Ordinal);
        _analyses = project.Documents.Select(document => new XamlAnalysis(document.Input.Syntax, document.Document, document.Output)).ToImmutableArray();
    }
    public XamlProjectRenameTarget? Prepare(string path, int offset, CancellationToken cancellationToken = default)
    {
        var analysis = _analyses.FirstOrDefault(item => item.Syntax.Path == path);
        if (analysis != null)
        {
            var name = new XamlRenameService(_compiler).Prepare(analysis, offset, cancellationToken);
            if (name != null) return new(name.Name, name.Span, "XamlName");
            var reference = XamlSymbolReferenceIndex.Create(analysis, _compiler, cancellationToken)
                .Where(item => item.Span.Contains(offset)).OrderBy(item => item.Span.Length).FirstOrDefault();
            return reference == null ? null : new(reference.RenameName, reference.Span, reference.Symbol.Kind.ToString());
        }
        var service = new CSharpLanguageService(_compilation, _editable); var symbol = service.ResolveSymbol(path, offset, cancellationToken);
        return symbol == null ? null : new(CSharpRenameSymbols.Name(CSharpRenameSymbols.RenameTarget(symbol)), service.GetSymbol(path, offset, cancellationToken)!.Span, symbol.Kind.ToString());
    }
    public XamlRenamePlan Rename(string path, int offset, string newName, CancellationToken cancellationToken = default)
    {
        EnsureSnapshot(cancellationToken);
        var analysis = _analyses.FirstOrDefault(item => item.Syntax.Path == path);
        if (analysis != null)
        {
            if (new XamlRenameService(_compiler).Prepare(analysis, offset, cancellationToken) != null)
                return RenameName(analysis, offset, newName, cancellationToken);
            newName = CSharpRenameService.ValidateName(newName);
            var reference = XamlSymbolReferenceIndex.Create(analysis, _compiler, cancellationToken)
                .Where(item => item.Span.Contains(offset)).OrderBy(item => item.Span.Length).FirstOrDefault()
                ?? throw new InvalidOperationException("Select a resolved XAML name, CLR type, namespace or member reference.");
            var selected = Original(reference.Symbol) ?? throw new InvalidOperationException("The selected XAML symbol has no declaration in this C# compilation.");
            var plan = RenameSymbol(PrepareSymbol(selected, reference.Span, reference.SymbolName(newName), cancellationToken), cancellationToken);
            return plan with { OldName = reference.RenameName, NewName = newName };
        }
        var service = new CSharpLanguageService(_compilation, _editable);
        if (service.ResolveSymbol(path, offset, cancellationToken) is IFieldSymbol field)
            foreach (var document in _analyses)
            {
                if (document.Document.ClassSymbol is not { } owner || !CSharpRenameSymbols.SameDeclaration(owner, field.ContainingType)) continue;
                var index = XamlNameReferenceIndex.Create(document, _compiler, cancellationToken);
                var declaration = index.Occurrences.FirstOrDefault(item => item.IsDeclaration && item.Name == field.Name && item.NameScopeId == document.Document.Root?.NameScopeId);
                if (declaration != null) return RenameName(document, declaration.Span.Start, newName, cancellationToken);
            }
        var symbol = service.ResolveSymbol(path, offset, cancellationToken) ?? throw new InvalidOperationException("Select a resolved C# identifier.");
        return RenameSymbol(PrepareSymbol(symbol, service.GetSymbol(path, offset, cancellationToken)!.Span, newName, cancellationToken), cancellationToken);
    }
    private CSharpRenamePending PrepareSymbol(ISymbol symbol, TextSpan span, string newName, CancellationToken token)
    {
        var service = new CSharpRenameService(_compilation, _editable);
        var pending = service.Prepare(symbol, span, newName, true, token);
        var framework = XamlFrameworkRenameChanges.Create(_compilation, pending.Targets, _editable, token);
        return framework.Names.IsEmpty ? pending : service.Prepare(symbol, span, pending.Plan.NewName, true, token, framework.Names).WithChanges(framework.Documents);
    }
    private XamlRenamePlan RenameSymbol(CSharpRenamePending pending, CancellationToken token)
    {
        var edits = pending.Plan.Documents.ToBuilder();
        foreach (var analysis in _analyses)
        {
            var changes = XamlSymbolReferenceIndex.Create(analysis, _compiler, token).Where(reference => pending.Targets.Contains(reference.Symbol))
                .Select(reference => new XamlTextChange(reference.Span, reference.Replacement(pending.Targets.MappedName(reference.Symbol)))).Distinct().OrderBy(change => change.Span.Start).ToImmutableArray();
            if (!changes.IsEmpty) edits.Add(new(analysis.Syntax.Path, analysis.Syntax.Text, analysis.Syntax.Version, changes));
        }
        var plan = pending.Plan with { Documents = edits.ToImmutable() };
        var (compilation, project) = Regenerate(plan.Documents, token);
        var verifier = pending.Verify(compilation, token);
        VerifyXaml(project, plan.Documents, verifier, token);
        return plan;
    }
    private XamlRenamePlan RenameName(XamlAnalysis analysis, int offset, string newName, CancellationToken token)
    {
        var plan = new XamlRenameService(_compiler).Rename(analysis, offset, newName, _analyses, token);
        var occurrence = XamlNameReferenceIndex.Create(analysis, _compiler, token).At(offset)!;
        var owner = analysis.Document.ClassSymbol == null ? null : Original(analysis.Document.ClassSymbol) as INamedTypeSymbol;
        var field = occurrence.NameScopeId == analysis.Document.Root?.NameScopeId ? owner?.GetMembers(plan.OldName).OfType<IFieldSymbol>().SingleOrDefault() : null;
        var targets = field == null ? CSharpRenameSymbols.Empty(newName) : CSharpRenameSymbols.Create(_compilation, field, newName, token);
        var observations = ImmutableArray.CreateBuilder<CSharpRenameObservation>();
        foreach (var tree in _compilation.SyntaxTrees.Where(tree => _editable.Contains(tree.FilePath)))
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var identifier in tree.GetRoot(token).DescendantTokens(descendIntoTrivia: true).Where(item => item.IsKind(SyntaxKind.IdentifierToken) && !item.IsMissing))
            {
                token.ThrowIfCancellationRequested();
                if (CSharpLanguageService.SymbolAt(model, identifier, token) is { } symbol) observations.Add(new(tree.FilePath, identifier.SpanStart, symbol));
            }
        }
        var pending = new CSharpRenamePending(_compilation, _editable, targets, plan, observations.ToImmutable());
        var (compilation, project) = Regenerate(plan.Documents, token);
        VerifyXaml(project, plan.Documents, pending.Verify(compilation, token), token);
        return plan;
    }
    private (CSharpCompilation Compilation, XamlProjectCompilation Project) Regenerate(ImmutableArray<XamlDocumentEdits> edits, CancellationToken token)
    {
        var byPath = edits.ToDictionary(edit => edit.Path, StringComparer.Ordinal); var application = _compiler.Types.Compilation;
        foreach (var tree in application.SyntaxTrees)
        {
            token.ThrowIfCancellationRequested();
            if (!byPath.TryGetValue(tree.FilePath, out var edit)) continue;
            if (!_editable.Contains(tree.FilePath) || tree.GetText(token).ToString() != edit.OriginalText)
                throw new InvalidOperationException("The C# rename snapshot is stale or read-only.");
            application = application.ReplaceSyntaxTree(tree, tree.WithChangedText(tree.GetText(token).WithChanges(edit.Changes.Select(change =>
                new Microsoft.CodeAnalysis.Text.TextChange(new(change.Span.Start, change.Span.Length), change.NewText)))));
        }
        var inputs = _project.Documents.Select(document => byPath.TryGetValue(document.Input.Syntax.Path, out var edit)
            ? document.Input with { Syntax = document.Input.Syntax.WithChanges(edit.Changes, edit.Version ?? document.Input.Syntax.Version, token) } : document.Input).ToImmutableArray();
        var compiler = new XamlCompilationSession(application, _compiler.Profile, _compiler.Options, projectDocuments: inputs);
        var project = compiler.CompileProject(inputs.Select(input => input.Syntax), token);
        if (!project.Success) throw new InvalidOperationException("The rename creates XAML or source-integration errors. No source was changed.");
        return (XamlCSharpCompilation.AddGeneratedSources(application, project, cancellationToken: token), project);
    }
    private void VerifyXaml(XamlProjectCompilation project, ImmutableArray<XamlDocumentEdits> edits, CSharpRenameVerification verifier, CancellationToken token)
    {
        var documents = project.Documents.ToDictionary(document => document.Input.Syntax.Path, StringComparer.Ordinal);
        var positions = new CSharpRenamePositions(edits);
        foreach (var analysis in _analyses)
        {
            token.ThrowIfCancellationRequested(); var updated = documents[analysis.Syntax.Path].Document;
            if (analysis.Document.ClassSymbol is { } originalClass && !verifier.Matches(originalClass, updated.ClassSymbol))
                throw new InvalidOperationException("The rename changes an XAML document's code-behind identity. No source was changed.");
            var symbols = updated.Symbols.ToLookup(symbol => (symbol.Span, symbol.Role));
            foreach (var previous in analysis.Document.Symbols)
            {
                token.ThrowIfCancellationRequested();
                var span = positions.Span(analysis.Syntax.Path, previous.Span);
                if (!symbols[(span, previous.Role)].Any(symbol => verifier.Matches(previous.Symbol, symbol.Symbol)))
                    throw new InvalidOperationException("The rename changes an XAML symbol binding in " + analysis.Syntax.Path + ". No source was changed.");
            }
            var objects = BoundDocumentTraversal.Objects(updated).ToLookup(obj => obj.Syntax.Span);
            foreach (var previous in BoundDocumentTraversal.Objects(analysis.Document))
            {
                token.ThrowIfCancellationRequested();
                var span = positions.Span(analysis.Syntax.Path, previous.Syntax.Span);
                if (!objects[span].Any(next => verifier.Matches(previous.Type, next.Type) &&
                    Optional(previous.Constructor, next.Constructor) && Optional(previous.FactoryMethod, next.FactoryMethod) && Assignments(previous, next)))
                    throw new InvalidOperationException("The rename changes XAML construction or member access in " + analysis.Syntax.Path + ". No source was changed.");
            }
            bool Optional(ISymbol? before, ISymbol? after) => before == null ? after == null : verifier.Matches(before, after);
            bool Assignments(BoundObject before, BoundObject after)
            {
                if (before.Assignments.Length != after.Assignments.Length) return false;
                for (var index = 0; index < before.Assignments.Length; index++)
                {
                    var old = before.Assignments[index]; var next = after.Assignments[index];
                    if (old.GetType() != next.GetType()) return false;
                    var oldMember = Member(old); var nextMember = Member(next);
                    if (oldMember == null) { if (nextMember != null) return false; }
                    else if (nextMember == null || oldMember.Kind != nextMember.Kind || oldMember.IsImplicitContent != nextMember.IsImplicitContent ||
                        !verifier.Matches(oldMember.Symbol, nextMember.Symbol) || !verifier.Matches(oldMember.ValueType, nextMember.ValueType) ||
                        !Optional(oldMember.Getter, nextMember.Getter) || !Optional(oldMember.Setter, nextMember.Setter)) return false;
                    if (old is BoundAddAssignment add && next is BoundAddAssignment nextAdd && !verifier.Matches(add.AddMethod, nextAdd.AddMethod)) return false;
                    if (old is BoundEventAssignment ev && next is BoundEventAssignment nextEvent && !Optional(ev.Handler, nextEvent.Handler)) return false;
                }
                return true;
            }
        }
        static BoundMember? Member(BoundAssignment assignment) => assignment switch
        { BoundSetAssignment set => set.Member, BoundAddAssignment add => add.Collection, BoundEventAssignment ev => ev.Event, BoundAdaptedSetAssignment adapted => adapted.Member, _ => null };
    }
    private ISymbol? Original(ISymbol symbol)
    {
        symbol = CSharpRenameSymbols.Normalize(symbol);
        return symbol.GetDocumentationCommentId() is { } id ? DocumentationCommentId.GetFirstSymbolForDeclarationId(id, _compilation) : null;
    }
    private void EnsureSnapshot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_project.Success || _compilation.GetDiagnostics(token).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Resolve project compilation errors before coordinated rename.");
        var known = _compiler.Types.Compilation.SyntaxTrees.Select(tree => tree.FilePath)
            .Concat(XamlCSharpCompilation.Sources(_project).Select(source => source.HintName)).ToImmutableHashSet(StringComparer.Ordinal);
        if (_compilation.SyntaxTrees.Any(tree => !known.Contains(tree.FilePath)))
            throw new InvalidOperationException("The compilation contains generated sources that this XAML session cannot regenerate.");
        if (_compiler.ProjectDocuments.Any(document => !_analyses.Any(analysis => analysis.Syntax.Path == document.Syntax.Path)))
            throw new InvalidOperationException("Coordinated rename requires the complete XAML project.");
    }
}
