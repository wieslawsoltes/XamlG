using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace XamlG.CSharp.Integration;

/// <summary>Framework source adaptation emitted once per project, independent of its host.
/// Diagnostics keep their original C# source locations, rather than pretending to be XAML errors.</summary>
public sealed record XamlSourceIntegrationResult(string Source, ImmutableArray<Diagnostic> Diagnostics)
{
    public static XamlSourceIntegrationResult Empty { get; } = new(string.Empty, ImmutableArray<Diagnostic>.Empty);
    public const string HintName = "XamlG.LoaderAdapters.g.cs";
    public bool Success => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
