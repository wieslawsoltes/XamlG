using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed record SelectorStepSyntax(SelectorStepKind Kind, string Name, string? Value, TextSpan Span)
{
    public SelectorListSyntax? Argument { get; init; }
    public int Step { get; init; }
    public int Offset { get; init; }
}
