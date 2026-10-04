using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XamlG.Roslyn;

namespace XamlG.Tooling.Navigation;

/// <summary>Finds symbol references in the project's loaded C# sources. Comments, strings and
/// same-spelled locals are not references. Metadata assemblies are never executed.</summary>
public sealed class XamlCSharpReferenceService(XamlCompilationSession compiler)
{
    public ImmutableArray<XamlDefinition> Find(ISymbol symbol, bool includeDeclaration = false, CancellationToken cancellationToken = default) =>
        FindCore(compiler.Types.Compilation, symbol, includeDeclaration, cancellationToken);

    public ImmutableArray<XamlDefinition> FindName(XamlAnalysis owner, string name,
        IEnumerable<XamlAnalysis> project, bool includeDeclaration = false, CancellationToken cancellationToken = default)
    {
        if (owner.Document.ClassSymbol == null || !owner.Output.Success) return ImmutableArray<XamlDefinition>.Empty;
        var compilation = compiler.Types.Compilation;
        var parse = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? new CSharpParseOptions(LanguageVersion.Preview);
        var augmented = compilation.AddSyntaxTrees(project.Where(a => a.Output.Success).GroupBy(a => a.Output.HintName, StringComparer.Ordinal)
            .Select(g => CSharpSyntaxTree.ParseText(g.First().Output.Source, parse, g.Key, cancellationToken: cancellationToken)));
        var field = augmented.GetTypeByMetadataName(owner.Document.ClassSymbol.MetadataName())?.GetMembers(name).OfType<IFieldSymbol>().SingleOrDefault();
        return field == null ? ImmutableArray<XamlDefinition>.Empty : FindCore(augmented, field, includeDeclaration, cancellationToken);
    }

    private ImmutableArray<XamlDefinition> FindCore(CSharpCompilation compilation, ISymbol selected, bool includeDeclaration, CancellationToken token)
    {
        var result = new List<XamlDefinition>();
        var symbol = selected.OriginalDefinition;
        var declarationId = symbol.GetDocumentationCommentId();
        bool Matches(ISymbol? candidate)
        {
            if (candidate == null) return false;
            candidate = candidate.OriginalDefinition;
            return SymbolEqualityComparer.Default.Equals(symbol, candidate) || declarationId != null &&
                declarationId == candidate.GetDocumentationCommentId() && Equals(symbol.ContainingAssembly?.Identity, candidate.ContainingAssembly?.Identity);
        }
        var originalTrees = compiler.Types.Compilation.SyntaxTrees.ToArray();
        foreach (var tree in originalTrees)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(tree.FilePath)) continue;
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot(token).DescendantNodes().OfType<SimpleNameSyntax>())
            {
                token.ThrowIfCancellationRequested();
                if (name.Identifier.ValueText != symbol.Name || !Matches(model.GetSymbolInfo(name, token).Symbol)) continue;
                Add(tree, name.Identifier.Span);
            }
        }
        if (includeDeclaration)
            foreach (var location in symbol.Locations.Where(l => l.IsInSource && l.SourceTree != null && originalTrees.Contains(l.SourceTree)))
                Add(location.SourceTree!, location.SourceSpan);
        return result.Distinct().OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Span.Start).ToImmutableArray();

        void Add(SyntaxTree tree, Microsoft.CodeAnalysis.Text.TextSpan span)
        {
            var lines = tree.GetText(token).Lines.GetLinePositionSpan(span);
            result.Add(new(tree.FilePath, new(span.Start, span.Length), new(lines.Start.Line, lines.Start.Character), new(lines.End.Line, lines.End.Character)));
        }
    }
}
