using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Generator;

/// <summary>The terminal cache value contains no Roslyn compilation, syntax node, symbol or semantic model.</summary>
internal sealed record GeneratorOutput(string Path, string Text, string HintName, string Source, ImmutableArray<XamlDiagnostic> Diagnostics);
