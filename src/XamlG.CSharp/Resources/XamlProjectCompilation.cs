using System.Collections.Immutable;
using XamlG.Compiler.Resources;

namespace XamlG.CSharp.Resources;

public sealed record XamlProjectCompilation(ImmutableArray<XamlProjectDocumentResult> Documents, XamlResourceCatalog Resources)
{
    public XamlProjectStatistics Statistics { get; init; } = new(0, 0, 0, 0);
    public bool Success => Documents.All(d => d.Output.Success);
}
