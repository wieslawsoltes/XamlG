using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;

internal static class MetadataDiagnosticCollector
{
    private static readonly SymbolDisplayFormat DisplayFormat = new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters, memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);
    public static void Collect(BindingContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in context.Symbols)
        {
            var symbol = occurrence.Symbol;
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }) continue;
            var name = symbol.ToDisplayString(DisplayFormat);
            foreach (var attribute in symbol.GetAttributes())
            {
                var metadata = attribute.AttributeClass?.ToDisplayString();
                if (metadata == "System.ObsoleteAttribute")
                {
                    var message = attribute.ConstructorArguments.FirstOrDefault().Value as string;
                    var severity = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is true ? XamlSeverity.Error : XamlSeverity.Warning;
                    var code = attribute.NamedArguments.FirstOrDefault(p => p.Key == "DiagnosticId").Value.Value as string ?? "XG2001";
                    Report(code, $"'{name}' is obsolete" + (string.IsNullOrEmpty(message) ? "." : ": " + message), severity);
                }
                else if (metadata == "System.Diagnostics.CodeAnalysis.ExperimentalAttribute")
                {
                    var code = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "XG2002";
                    var message = attribute.NamedArguments.FirstOrDefault(p => p.Key == "Message").Value.Value as string;
                    Report(code, $"'{name}' is for evaluation purposes only and is subject to change or removal in future updates" +
                        (string.IsNullOrEmpty(message) ? "." : ": '" + message + "'."), XamlSeverity.Warning);
                }
            }
            void Report(string code, string message, XamlSeverity severity)
            {
                if (seen.Add(code + "|" + occurrence.Span + "|" + name)) context.Report(code, message, occurrence.Span, severity);
            }
        }
    }
}
