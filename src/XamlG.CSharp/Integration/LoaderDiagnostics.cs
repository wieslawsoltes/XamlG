using Microsoft.CodeAnalysis;

namespace XamlG.CSharp.Integration;

internal static class LoaderDiagnostics
{
    public static Diagnostic Error(Location location, string message) => Diagnostic.Create(
        new DiagnosticDescriptor("XG3400", "Unsupported compiled loader operation", "{0}", "XamlG", DiagnosticSeverity.Error, true), location, message);
}
