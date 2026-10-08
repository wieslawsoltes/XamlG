namespace XamlG.Playground;

public sealed record CSharpEditorQuery(string Path, string Text, int Offset, string Kind, string? TargetPath = null);
public sealed record CSharpNavigationRequest(string Path, int StartLine, int StartColumn, int EndLine, int EndColumn);
