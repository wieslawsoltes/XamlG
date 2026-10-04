using System.Collections.Immutable;

namespace XamlG.LanguageServer.Diagnostics;

internal sealed record LspDiagnosticCacheEntry(string ResultId, ImmutableArray<LspDiagnosticItem> Items,
    long Characters, LinkedListNode<string> Node);
