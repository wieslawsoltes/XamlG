namespace XamlG.Syntax;
public sealed record XamlTriviaSyntax(string Kind, TextSpan FullSpan) : XamlSyntaxNode(FullSpan);
