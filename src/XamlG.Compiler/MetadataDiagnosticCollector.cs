using Microsoft.CodeAnalysis;
using System.Runtime.CompilerServices;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;

internal static class MetadataDiagnosticCollector
{
    private static readonly SymbolDisplayFormat DisplayFormat = new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters, memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);
    // Symbols and their attributes are immutable. Cache descriptions, including
    // negative results, without retaining compilations or occurrence locations.
    private static readonly ConditionalWeakTable<ISymbol, MetadataDiagnostics> Cache = new();
    private sealed class MetadataDiagnostics(string name, XamlDiagnostic[] diagnostics)
    {
        public static readonly MetadataDiagnostics Empty = new(string.Empty, []);
        public string Name { get; } = name;
        public XamlDiagnostic[] Diagnostics { get; } = diagnostics;
    }
    public static void Collect(BindingContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in context.Symbols)
        {
            var symbol = occurrence.Symbol;
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }) continue;
            var metadata = Cache.GetValue(symbol, static value => Read(value));
            foreach (var diagnostic in metadata.Diagnostics)
            {
                if (seen.Add(diagnostic.Code + "|" + occurrence.Span + "|" + metadata.Name))
                    context.Report(diagnostic.Code, diagnostic.Message, occurrence.Span, diagnostic.Severity);
            }
        }
    }

    private static MetadataDiagnostics Read(ISymbol symbol)
    {
        string? name = null;
        List<XamlDiagnostic>? diagnostics = null;
        string Name() => name ??= symbol.ToDisplayString(DisplayFormat);
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeType) continue;
            if (attributeType.HasMetadataName("System.ObsoleteAttribute"))
            {
                var message = attribute.ConstructorArguments.FirstOrDefault().Value as string;
                var severity = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is true ? XamlSeverity.Error : XamlSeverity.Warning;
                var code = attribute.NamedArguments.FirstOrDefault(p => p.Key == "DiagnosticId").Value.Value as string ?? "XG2001";
                (diagnostics ??= new()).Add(new(code, $"'{Name()}' is obsolete" + (string.IsNullOrEmpty(message) ? "." : ": " + message), default, severity));
            }
            else if (attributeType.HasMetadataName("System.Diagnostics.CodeAnalysis.ExperimentalAttribute"))
            {
                var code = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "XG2002";
                var message = attribute.NamedArguments.FirstOrDefault(p => p.Key == "Message").Value.Value as string;
                (diagnostics ??= new()).Add(new(code, $"'{Name()}' is for evaluation purposes only and is subject to change or removal in future updates" +
                    (string.IsNullOrEmpty(message) ? "." : ": '" + message + "'."), default, XamlSeverity.Warning));
            }
        }
        return diagnostics == null ? MetadataDiagnostics.Empty : new(name!, diagnostics.ToArray());
    }
}
