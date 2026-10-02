namespace XamlG.Syntax;
public sealed record XamlAttributeSyntax(string Name, string Value, TextSpan NameSpan, TextSpan ValueSpan, TextSpan Span, char Quote)
{
    public bool IsNamespace => Name == "xmlns" || Name.StartsWith("xmlns:", StringComparison.Ordinal);
    public string LocalName => Name.Substring(Name.IndexOf(':') + 1);
}
