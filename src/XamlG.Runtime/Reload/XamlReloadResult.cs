namespace XamlG.Runtime.Reload;

public sealed record XamlReloadResult(bool Applied, long Revision, object Root, string? Error, IReadOnlyList<string> Warnings);
