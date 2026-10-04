using XamlG.Syntax;

namespace XamlG.Compiler.References;

/// <summary>A framework-recognized name reference with a complete, raw XML source range.</summary>
public sealed record XamlNameReference(string Name, TextSpan Span);
