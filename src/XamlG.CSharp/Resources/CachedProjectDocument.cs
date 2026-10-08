using XamlG.Compiler;

namespace XamlG.CSharp.Resources;

internal sealed class CachedProjectDocument(XamlProjectDocument input, BoundDocument document)
{
    public XamlProjectDocument Input { get; } = input;
    public BoundDocument Document { get; } = document;
    public XamlEmissionResult? Output { get; set; }
    public XamlEmissionResult? OutputWithServices { get; set; }
}
