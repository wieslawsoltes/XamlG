using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

public sealed record BindingPathSyntax(ImmutableArray<BindingPathSegment> Segments, TextSpan Span);
