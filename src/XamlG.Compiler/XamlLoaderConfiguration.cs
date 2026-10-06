namespace XamlG.Compiler;

/// <summary>Declarative source-loader ABI, resolved against actual Roslyn symbols.</summary>
public sealed record XamlLoaderConfiguration(string TypeMetadataName, string MethodName)
{
    public string ResourceScheme { get; init; } = "xamlg";
    /// <summary>Only referenced assemblies exposing this compiled index may use the
    /// framework's existing binary resource loader; local/unknown resources never do.</summary>
    public string? LegacyIndexMetadataName { get; init; }
}
