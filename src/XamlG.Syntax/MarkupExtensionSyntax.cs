using System.Collections.Immutable;
namespace XamlG.Syntax;
public sealed record MarkupExtensionSyntax(string Name, ImmutableArray<MarkupArgumentSyntax> Arguments, TextSpan Span)
{
    public TextSpan? NameSpan { get; init; }
}
