using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.Workspaces;

/// <summary>An immutable evaluated project snapshot. XAML documents share symbols and resource-linking policy.</summary>
public sealed record XamlWorkspaceProject(Project Project, XamlCompilationSession Compiler,
    ImmutableDictionary<string, XamlSyntaxTree> Documents)
{
    public ImmutableArray<XamlAnalysis> Analyze(CancellationToken cancellationToken = default) =>
        Compiler.AnalyzeProject(Documents.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Value), cancellationToken);

    public XamlWorkspaceProject WithDocumentText(string path, string text)
    {
        if (!Documents.TryGetValue(path, out var previous)) throw new ArgumentException("The document does not belong to this project snapshot.", nameof(path));
        var updated = XamlSyntaxTree.Parse(text, previous.Path, options: previous.Options, version: checked(previous.Version + 1));
        return this with { Documents = Documents.SetItem(path, updated) };
    }
}
