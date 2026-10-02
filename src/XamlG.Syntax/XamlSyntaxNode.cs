namespace XamlG.Syntax;
public abstract record XamlSyntaxNode(TextSpan FullSpan)
{
    public TextSpan Span => FullSpan;
}
