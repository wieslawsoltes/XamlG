using System.Collections.Immutable;

namespace XamlG.Workspaces;

public sealed record XamlWorkspaceOptions
{
    public string Framework { get; init; } = "Auto";
    public bool AllowProjectEvaluation { get; init; }
    public bool RunApplicationSourceGenerators { get; init; } = true;
    public ImmutableDictionary<string, string> GlobalProperties { get; init; } = ImmutableDictionary<string, string>.Empty;
}
