using System.Collections.Immutable;

namespace XamlG.Tooling.Editing;

internal sealed record XamlWorkspaceHistoryEntry(string Description,
    ImmutableDictionary<string, string> Before, ImmutableDictionary<string, string> After, long Characters);
