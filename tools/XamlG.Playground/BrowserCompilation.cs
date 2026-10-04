using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Tooling;

namespace XamlG.Playground;

public sealed record BrowserCompilation(XamlAnalysis Analysis, CSharpCompilation Compilation,
    ImmutableArray<PlaygroundDiagnostic> Diagnostics, double ElapsedMilliseconds)
{
    public XamlCompilationSession? AuthoringCompiler { get; init; }
    public XamlProjectCompilation? Project { get; init; }
    public long ResourceRevision { get; init; }
    public bool Success => Analysis.Output.Success && (Project?.Success ?? true) && !Diagnostics.Any(d => d.Severity == "Error");
}
