using System.Collections.Immutable;
using XamlG.Tooling;

namespace XamlG.LanguageServer;

/// <summary>The actual overlay compiler and bound documents must travel together: their symbols share identity.</summary>
public sealed record LspWorkspaceAnalysis(XamlCompilationSession Compiler, ImmutableArray<XamlAnalysis> Documents);
