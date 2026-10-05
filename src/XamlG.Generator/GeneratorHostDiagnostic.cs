using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace XamlG.Generator;

/// <summary>A value-only diagnostic projection; terminal generator caches retain no Roslyn source trees.</summary>
internal sealed record GeneratorHostDiagnostic(string Code, string Message, string Path, TextSpan Span, LinePositionSpan Lines)
{
    public static GeneratorHostDiagnostic From(Diagnostic diagnostic)
    {
        var line = diagnostic.Location.GetLineSpan();
        return new(diagnostic.Id, diagnostic.GetMessage(), line.Path, diagnostic.Location.SourceSpan, line.Span);
    }
    public Diagnostic Create() => Diagnostic.Create(
        new DiagnosticDescriptor(Code, "XAML source integration", "{0}", "XamlG", DiagnosticSeverity.Error, true),
        Location.Create(Path, Span, Lines), Message);
}
