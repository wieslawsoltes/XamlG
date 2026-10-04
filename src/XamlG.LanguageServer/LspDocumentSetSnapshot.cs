using System.Collections.Immutable;

namespace XamlG.LanguageServer;

/// <summary>A coherent open-buffer set, separate from each client's document version and the project compilation revision.</summary>
public sealed record LspDocumentSetSnapshot(long Revision, ImmutableArray<LspDocumentSnapshot> Documents);
