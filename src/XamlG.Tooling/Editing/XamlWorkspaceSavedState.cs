namespace XamlG.Tooling.Editing;

public sealed record XamlWorkspaceSavedState(int Version, XamlWorkspaceSnapshot Current,
    IReadOnlyList<XamlWorkspaceHistoryEntry> Undo, IReadOnlyList<XamlWorkspaceHistoryEntry> Redo);
