using System.Collections.Immutable;
using XamlG.Compiler;

namespace XamlG.CSharp.Resources;

internal sealed class CachedProjectDocument(XamlProjectDocument input, BoundDocument document)
{
    public XamlProjectDocument Input { get; } = input;
    public BoundDocument Document { get; } = document;
    public XamlEmissionResult? Output { get; set; }
    public XamlEmissionResult? OutputWithHelpers { get; set; }
    public ImmutableArray<SharedGeneratedSource> PublishedSources { get; set; } = ImmutableArray<SharedGeneratedSource>.Empty;
}
