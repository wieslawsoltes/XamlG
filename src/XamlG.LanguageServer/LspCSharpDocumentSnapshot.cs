using Microsoft.CodeAnalysis.Text;

namespace XamlG.LanguageServer;

public sealed record LspCSharpDocumentSnapshot(string Uri, string Path, int Version, SourceText Text);
