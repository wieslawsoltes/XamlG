using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed record SelectorSequenceSyntax(ImmutableArray<SelectorStepSyntax> Steps, TextSpan Span);
