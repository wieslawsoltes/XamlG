namespace XamlG.Syntax;

public sealed record MarkupArgumentSyntax(string? Name, string Value, TextSpan Span)
{
    /// <summary>Exact value range in the parser input, excluding whitespace/outer quotes.
    /// Null for synthetic arguments. XML hosts must map decoded offsets back to raw source.</summary>
    public TextSpan? ValueSpan { get; init; }
}
