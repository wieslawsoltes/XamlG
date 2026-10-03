namespace XamlG.Runtime.Design;

/// <summary>One source-revision-checked visual edit. A host applies all properties in a single undo unit.</summary>
public sealed record XamlVisualEdit(XamlSourceInfo Source, IReadOnlyDictionary<string, string> Properties);
