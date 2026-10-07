using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

public sealed record BindingPathSegment(BindingPathKind Kind, string Name, TextSpan Span)
{
    public bool AcceptsNull { get; init; }
    public ImmutableArray<string> Arguments { get; init; } = ImmutableArray<string>.Empty;
}
