namespace XamlG.Runtime.Reload;

/// <summary>Apply mutates only the detached candidate. AfterCommit is reserved for focus/layout-dependent restoration.</summary>
public sealed record XamlStateTransferOperation(Action Apply, Action? AfterCommit = null);
