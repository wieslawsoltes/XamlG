using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace XamlG.CSharp.Integration;

/// <summary>Forwards tree-identity-based analyzer configuration after feature-only tree replacement.</summary>
internal sealed class RemappedSyntaxTreeOptionsProvider(SyntaxTreeOptionsProvider inner,
    ImmutableDictionary<SyntaxTree, SyntaxTree> originals) : SyntaxTreeOptionsProvider
{
    public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) =>
        inner.IsGenerated(Original(tree), cancellationToken);
    public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken,
        out ReportDiagnostic severity) => inner.TryGetDiagnosticValue(Original(tree), diagnosticId, cancellationToken, out severity);
    public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken,
        out ReportDiagnostic severity) => inner.TryGetGlobalDiagnosticValue(diagnosticId, cancellationToken, out severity);
    private SyntaxTree Original(SyntaxTree tree) => originals.TryGetValue(tree, out var original) ? original : tree;
}
