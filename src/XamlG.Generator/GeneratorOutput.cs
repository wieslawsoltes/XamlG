using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Generator;

internal sealed record GeneratorOutput(string Path, string Text, string HintName, string Source, ImmutableArray<XamlDiagnostic> Diagnostics)
{
    public ImmutableArray<GeneratorHostDiagnostic> HostDiagnostics { get; init; } = ImmutableArray<GeneratorHostDiagnostic>.Empty;
}
