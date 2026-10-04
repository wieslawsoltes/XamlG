using System.Collections.Immutable;
using XamlG.CSharp.Resources;

namespace XamlG.Tooling.Refactoring;

/// <summary>Text edits address pre-move buffers, as required by workspace/willRenameFiles.
/// OriginalDocuments form the optimistic read set; UpdatedDocuments are the validated replacement project.</summary>
public sealed record XamlFileRenamePlan(ImmutableArray<XamlDocumentMove> Moves,
    ImmutableArray<XamlDocumentEdits> Documents, ImmutableArray<XamlProjectDocument> OriginalDocuments,
    ImmutableArray<XamlProjectDocument> UpdatedDocuments)
{
    public StringComparer PathComparer { get; init; } = StringComparer.Ordinal;
}
