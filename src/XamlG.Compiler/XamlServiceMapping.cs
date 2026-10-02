namespace XamlG.Compiler;
public sealed record XamlServiceMapping(string InterfaceMetadataName, XamlServiceKind Kind, string? NamespaceItemMetadataName = null);
