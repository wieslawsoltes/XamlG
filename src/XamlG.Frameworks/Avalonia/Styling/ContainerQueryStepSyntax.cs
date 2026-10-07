using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed record ContainerQueryStepSyntax(ContainerQueryStepKind Kind, string Comparison, double Value, TextSpan Span);
