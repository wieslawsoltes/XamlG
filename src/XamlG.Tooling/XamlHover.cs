using XamlG.Syntax;

namespace XamlG.Tooling;

public sealed record XamlHover(TextSpan Span, string Signature, string Documentation);
