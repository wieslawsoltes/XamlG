using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

internal sealed record XamlResourceSourceSite(string Text, TextSpan Span, char? Quote, bool CanRewrite);
