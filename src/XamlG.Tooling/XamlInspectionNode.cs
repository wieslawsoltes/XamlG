using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Tooling;

public sealed record XamlInspectionNode(string Id, string Kind, string Label, TextSpan Span,
    string? TypeName, ImmutableArray<XamlInspectionNode> Children);
