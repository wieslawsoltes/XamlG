using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.Workspaces;

/// <summary>An immutable evaluated project snapshot. All XAML documents share the same Roslyn symbol universe.</summary>
public sealed record XamlWorkspaceProject(Project Project, XamlCompilationSession Compiler,
    ImmutableDictionary<string, XamlSyntaxTree> Documents)
{
    public ImmutableArray<XamlAnalysis> Analyze(CancellationToken cancellationToken = default)
    {
        var result = ImmutableArray.CreateBuilder<XamlAnalysis>();
        foreach (var document in Documents.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(Compiler.Analyze(document.Value, cancellationToken));
        }
        return result.ToImmutable();
    }

    public XamlWorkspaceProject WithDocumentText(string path, string text)
    {
        if (!Documents.TryGetValue(path, out var previous)) throw new ArgumentException("The document does not belong to this project snapshot.", nameof(path));
        var updated = XamlSyntaxTree.Parse(text, previous.Path, options: previous.Options, version: checked(previous.Version + 1));
        return this with { Documents = Documents.SetItem(path, updated) };
    }
}
