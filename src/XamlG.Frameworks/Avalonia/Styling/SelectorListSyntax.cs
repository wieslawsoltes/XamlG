using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed record SelectorListSyntax(ImmutableArray<SelectorSequenceSyntax> Selectors, TextSpan Span);
