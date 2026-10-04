using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

/// <summary>Renames a statically resolved XAML name and its generated C# field references.
/// It produces source edits, never writes files or performs textual replacement in application code.</summary>
public sealed class XamlRenameService(XamlCompilationSession compiler)
{
    public XamlNameOccurrence? Prepare(XamlAnalysis analysis, int position, CancellationToken cancellationToken = default)
    {
        if (!analysis.Document.Success || !analysis.Output.Success) return null;
        var index = XamlNameReferenceIndex.Create(analysis, compiler, cancellationToken);
        var occurrence = index.At(position);
        return occurrence != null && index.Declaration(occurrence) != null ? occurrence : null;
    }

    public XamlRenamePlan Rename(XamlAnalysis analysis, int position, string newName,
        IEnumerable<XamlAnalysis>? projectAnalyses = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || !SyntaxFacts.IsValidIdentifier(newName) || newName.StartsWith("@", StringComparison.Ordinal) || SyntaxFacts.GetKeywordKind(newName) != SyntaxKind.None)
            throw new ArgumentException("The new name must be an unescaped C# identifier, not a keyword.", nameof(newName));
        if (!analysis.Document.Success || !analysis.Output.Success) throw new InvalidOperationException("Resolve compiler errors before renaming a XAML name.");
        var index = XamlNameReferenceIndex.Create(analysis, compiler, cancellationToken);
        var occurrence = index.At(position) ?? throw new InvalidOperationException("The position is not a statically resolved XAML name.");
        var declaration = index.Declaration(occurrence) ?? throw new InvalidOperationException("The referenced name has no declaration in this namescope.");
        if (index.Occurrences.Any(o => o.IsDeclaration && o.Name == newName && o.NameScopeId == declaration.NameScopeId && o.Span != declaration.Span))
            throw new InvalidOperationException("The new name already exists in this template namescope.");
        if (occurrence.Name == newName) return new(occurrence.Name, newName, occurrence.Span, ImmutableArray<XamlDocumentEdits>.Empty);
        var documents = ImmutableArray.CreateBuilder<XamlDocumentEdits>();
        documents.Add(new(analysis.Syntax.Path, analysis.Syntax.Text, analysis.Syntax.Version,
            index.References(occurrence).Select(o => new XamlTextChange(o.Span, newName)).Distinct().OrderBy(e => e.Span.Start).ToImmutableArray()));
        if (analysis.Document.ClassSymbol != null && declaration.NameScopeId == analysis.Document.Root!.NameScopeId)
            AddCodeBehindEdits(analysis, occurrence.Name, newName, projectAnalyses ?? new[] { analysis }, documents, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(occurrence.Name, newName, occurrence.Span, documents.ToImmutable());
    }

    private void AddCodeBehindEdits(XamlAnalysis owner, string oldName, string newName, IEnumerable<XamlAnalysis> analyses,
        ImmutableArray<XamlDocumentEdits>.Builder edits, CancellationToken token)
    {
        var compilation = compiler.Types.Compilation;
        var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? new CSharpParseOptions(LanguageVersion.Preview);
        var originalTrees = compilation.SyntaxTrees.ToArray();
        var generated = analyses.Where(a => a.Output.Success).GroupBy(a => a.Output.HintName, StringComparer.Ordinal)
            .Select(g => CSharpSyntaxTree.ParseText(g.First().Output.Source, parseOptions, g.Key, cancellationToken: token)).ToArray();
        compilation = compilation.AddSyntaxTrees(generated);
        var type = compilation.GetTypeByMetadataName(owner.Document.ClassSymbol!.MetadataName())
            ?? throw new InvalidOperationException("The code-behind class could not be resolved in the generated compilation.");
        var field = type.GetMembers(oldName).OfType<IFieldSymbol>().SingleOrDefault();
        if (field == null) throw new InvalidOperationException("No generated or explicit code-behind field was found for this XAML name.");
        if (type.Members(newName).Any()) throw new InvalidOperationException("The new name conflicts with a code-behind or inherited member.");
        // A name collision with a local variable can change binding after rename even though
        // the field symbol was correct beforehand. Reject those collisions before producing edits.
        foreach (var tree in originalTrees)
        {
            token.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var changes = ImmutableArray.CreateBuilder<XamlTextChange>();
            foreach (var node in tree.GetRoot(token).DescendantNodes())
            {
                token.ThrowIfCancellationRequested();
                Microsoft.CodeAnalysis.SyntaxToken identifier;
                if (node is IdentifierNameSyntax name && name.Identifier.ValueText == oldName && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, token).Symbol, field))
                {
                    identifier = name.Identifier;
                    if (!(name.Parent is MemberAccessExpressionSyntax access && ReferenceEquals(access.Name, name)) &&
                        name.Parent is not MemberBindingExpressionSyntax && model.LookupSymbols(name.SpanStart, name: newName).Any())
                        throw new InvalidOperationException("The new name would capture an existing local or parameter in C# code.");
                }
                else if (node is VariableDeclaratorSyntax variable && variable.Identifier.ValueText == oldName && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(variable, token), field)) identifier = variable.Identifier;
                else continue;
                if (string.IsNullOrEmpty(tree.FilePath)) throw new InvalidOperationException("A referenced C# syntax tree has no editable file path.");
                changes.Add(new(new(identifier.SpanStart, identifier.Span.Length), newName));
            }
            if (changes.Count != 0)
                edits.Add(new(tree.FilePath, tree.GetText(token).ToString(), null, changes.OrderBy(c => c.Span.Start).ToImmutableArray()));
        }
    }
}
