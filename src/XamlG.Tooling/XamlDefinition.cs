using XamlG.Syntax;

namespace XamlG.Tooling;

public sealed record XamlDefinition(string Path, TextSpan Span, SourceLinePosition Start, SourceLinePosition End);
