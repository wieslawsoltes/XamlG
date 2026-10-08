namespace XamlG.Syntax;

public sealed record MarkupArgumentSyntax(string? Name, string Value, TextSpan Span)
{
    /// <summary>Exact named-argument range, excluding whitespace and the equals sign.</summary>
    public TextSpan? NameSpan { get; init; }
    /// <summary>Exact value range in the parser input, excluding whitespace/outer quotes.
    /// Null for synthetic arguments. XML hosts must map decoded offsets back to raw source.</summary>
    public TextSpan? ValueSpan { get; init; }
}
