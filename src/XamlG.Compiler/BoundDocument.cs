using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundDocument(XamlSyntaxTree Syntax, BoundObject? Root, string? ClassName, INamedTypeSymbol? ClassSymbol,
    string ClassModifier, ImmutableArray<XamlDiagnostic> Diagnostics, ImmutableArray<BoundSymbolInfo> Symbols,
    XamlFrameworkProfile Profile, XamlCompilerOptions Options)
{
    public BoundRuntimeConfiguration Runtime { get; init; } = BoundRuntimeConfiguration.Empty;
    public bool Success => Root != null && !Diagnostics.Any(d => d.Severity == XamlSeverity.Error);
}
