using System.Collections.Immutable;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.LanguageServer.Diagnostics;

internal static class LspDiagnosticProjection
{
    public static ImmutableArray<LspDiagnosticItem> Create(XamlAnalysis analysis, CancellationToken token = default)
    {
        var result = ImmutableArray.CreateBuilder<LspDiagnosticItem>(analysis.Output.Diagnostics.Length);
        foreach (var diagnostic in analysis.Output.Diagnostics)
        {
            token.ThrowIfCancellationRequested();
            result.Add(new(LspConversions.Range(analysis.Syntax, diagnostic.Span),
                diagnostic.Severity switch { XamlSeverity.Error => 1, XamlSeverity.Warning => 2, XamlSeverity.Info => 3, _ => 4 },
                diagnostic.Code, "XamlG", diagnostic.Message));
        }
        return result.ToImmutable();
    }
}
