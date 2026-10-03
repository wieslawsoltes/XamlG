using XamlG.Syntax;

namespace XamlG.LanguageServer;

public sealed record LspDocumentSnapshot(string Uri, int Version, XamlSyntaxTree Syntax);
