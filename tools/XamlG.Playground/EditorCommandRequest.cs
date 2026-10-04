namespace XamlG.Playground;

/// <summary>An editor command is anchored to the exact UTF-16 buffer from which it was invoked.</summary>
public sealed record EditorCommandRequest(string Command, string Path, string Text, int Start, int Length);
