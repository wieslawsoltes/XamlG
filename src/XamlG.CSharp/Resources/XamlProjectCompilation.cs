using System.Collections.Immutable;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Integration;

namespace XamlG.CSharp.Resources;

public sealed record XamlProjectCompilation(ImmutableArray<XamlProjectDocumentResult> Documents, XamlResourceCatalog Resources)
{
    public XamlProjectStatistics Statistics { get; init; } = new(0, 0, 0, 0);
    public XamlSourceIntegrationResult SourceIntegration { get; init; } = XamlSourceIntegrationResult.Empty;
    public bool Success => Documents.All(d => d.Output.Success) && SourceIntegration.Success;
}
