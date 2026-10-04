using System.Collections.Immutable;
using XamlG.Syntax;
namespace XamlG.CSharp;
public sealed record XamlEmissionResult(string HintName, string Source, string FactoryTypeName, string? BuildMethodName, string PopulateMethodName, ImmutableArray<XamlDiagnostic> Diagnostics, ImmutableArray<XamlSourceMapping> SourceMappings)
{
    /// <summary>Reflection metadata name, including '+' for nested code-behind types.
    /// FactoryTypeName remains the C# source name for generated typeof expressions.</summary>
    public string FactoryMetadataName { get; init; } = FactoryTypeName;
    public bool Success => !Diagnostics.Any(d => d.Severity == XamlSeverity.Error) && Source.Length != 0;
}
