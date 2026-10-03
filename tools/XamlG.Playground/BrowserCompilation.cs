using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Tooling;

namespace XamlG.Playground;

public sealed record BrowserCompilation(XamlAnalysis Analysis, CSharpCompilation Compilation,
    ImmutableArray<PlaygroundDiagnostic> Diagnostics, double ElapsedMilliseconds)
{
    public bool Success => Analysis.Output.Success && !Diagnostics.Any(d => d.Severity == "Error");
}
