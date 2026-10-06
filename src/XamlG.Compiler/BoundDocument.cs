using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundDocument(XamlSyntaxTree Syntax, BoundObject? Root, string? ClassName, INamedTypeSymbol? ClassSymbol,
    string ClassModifier, ImmutableArray<XamlDiagnostic> Diagnostics, ImmutableArray<BoundSymbolInfo> Symbols,
    XamlFrameworkProfile Profile, XamlCompilerOptions Options)
{
    public BoundRuntimeConfiguration Runtime { get; init; } = BoundRuntimeConfiguration.Empty;
    /// <summary>False when code generation must use an external factory rather than add class members.</summary>
    public bool CanAugmentClass { get; init; } = true;
    public bool Success => Root != null && !Diagnostics.Any(d => d.Severity == XamlSeverity.Error);
}
