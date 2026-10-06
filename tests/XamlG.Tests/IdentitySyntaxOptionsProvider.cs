using Microsoft.CodeAnalysis;

namespace XamlG.Tests;

internal sealed class IdentitySyntaxOptionsProvider(SyntaxTree original) : SyntaxTreeOptionsProvider
{
    public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) =>
        ReferenceEquals(tree, original) ? GeneratedKind.MarkedGenerated : GeneratedKind.NotGenerated;
    public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
    {
        severity = ReportDiagnostic.Error;
        return ReferenceEquals(tree, original) && diagnosticId == "CS0169";
    }
    public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
    {
        severity = ReportDiagnostic.Suppress;
        return diagnosticId == "CS1591";
    }
}
