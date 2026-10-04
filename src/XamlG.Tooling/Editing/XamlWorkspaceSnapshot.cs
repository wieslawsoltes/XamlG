using System.Collections.Immutable;

namespace XamlG.Tooling.Editing;

/// <summary>An immutable multi-language source snapshot. Revision never moves backward, including undo.</summary>
public sealed record XamlWorkspaceSnapshot(long Revision, ImmutableDictionary<string, string> Documents);
