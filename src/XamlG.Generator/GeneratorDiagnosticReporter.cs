using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using XamlG.Syntax;

namespace XamlG.Generator;

internal static class GeneratorDiagnosticReporter
{
    public static Diagnostic Create(string path, SourceText text, XamlDiagnostic diagnostic)
    {
        var start = Math.Min(diagnostic.Span.Start, text.Length);
        var span = new Microsoft.CodeAnalysis.Text.TextSpan(start, Math.Min(diagnostic.Span.Length, text.Length - start));
        var location = Location.Create(path, span, text.Lines.GetLinePositionSpan(span));
        var severity = diagnostic.Severity switch
        {
            XamlSeverity.Hidden => DiagnosticSeverity.Hidden,
            XamlSeverity.Info => DiagnosticSeverity.Info,
            XamlSeverity.Warning => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Error
        };
        var descriptor = new DiagnosticDescriptor(diagnostic.Code, "XAML compilation", "{0}", "XamlG", severity, true);
        return Diagnostic.Create(descriptor, location, diagnostic.Message);
    }
}
