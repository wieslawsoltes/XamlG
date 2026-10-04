namespace XamlG.Syntax;

/// <summary>A parsed extension whose offsets are relative to the decoded XML value map.</summary>
public sealed record XamlMarkupOccurrence(MarkupExtensionSyntax Syntax, XamlDecodedTextMap SourceMap);
