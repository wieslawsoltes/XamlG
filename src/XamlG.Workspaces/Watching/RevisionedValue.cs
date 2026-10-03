namespace XamlG.Workspaces.Watching;

public sealed record RevisionedValue<T>(long Revision, T Value);
