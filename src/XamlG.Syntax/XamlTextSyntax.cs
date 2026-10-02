namespace XamlG.Syntax;
public sealed record XamlTextSyntax(string Value, bool IsCData, TextSpan FullSpan) : XamlSyntaxNode(FullSpan);
