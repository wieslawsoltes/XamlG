using XamlG.Tooling;

namespace XamlG.LanguageServer;

internal sealed record LspCompilationSnapshot(long Revision, XamlCompilationSession Compiler);
