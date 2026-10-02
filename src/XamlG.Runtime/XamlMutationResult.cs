namespace XamlG.Runtime;
public sealed record XamlMutationResult(bool Applied, long Revision, string? Error);
