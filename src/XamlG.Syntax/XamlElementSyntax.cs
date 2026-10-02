using System.Collections.Immutable;
namespace XamlG.Syntax;

public sealed record XamlElementSyntax(string Name, TextSpan NameSpan, TextSpan OpenTagSpan, TextSpan CloseTagSpan, ImmutableArray<XamlAttributeSyntax> Attributes, ImmutableArray<XamlSyntaxNode> Children, bool IsSelfClosing, TextSpan FullSpan) : XamlSyntaxNode(FullSpan)
{
    public string LocalName => Name.Substring(Name.IndexOf(':') + 1);
    public TextSpan EndNameSpan { get; init; }
    public IEnumerable<XamlElementSyntax> DescendantsAndSelf()
    {
        var pending = new Stack<XamlElementSyntax>(); pending.Push(this);
        while (pending.Count != 0)
        {
            var current = pending.Pop(); yield return current;
            for (var i = current.Children.Length - 1; i >= 0; i--)
                if (current.Children[i] is XamlElementSyntax element) pending.Push(element);
        }
    }
}
