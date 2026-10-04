using XamlG.Compiler;

namespace XamlG.CSharp.Resources;

public sealed record XamlProjectDocumentResult(XamlProjectDocument Input, string? ResourceUri,
    BoundDocument Document, XamlEmissionResult Output);
