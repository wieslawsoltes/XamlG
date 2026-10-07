using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed record ContainerQuerySyntax(ImmutableArray<ContainerQueryStepSyntax> Steps, TextSpan Span);
