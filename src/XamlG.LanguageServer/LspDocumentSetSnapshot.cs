using System.Collections.Immutable;

namespace XamlG.LanguageServer;

/// <summary>A coherent XAML/C# open-buffer set; each client version remains independent of this revision.</summary>
public sealed record LspDocumentSetSnapshot(long Revision, ImmutableArray<LspDocumentSnapshot> Documents)
{
    public ImmutableArray<LspCSharpDocumentSnapshot> CSharpDocuments { get; init; } = ImmutableArray<LspCSharpDocumentSnapshot>.Empty;
}
