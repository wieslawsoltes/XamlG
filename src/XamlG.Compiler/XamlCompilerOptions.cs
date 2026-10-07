using XamlG.Compiler.Resources;

namespace XamlG.Compiler;

public sealed record XamlCompilerOptions
{
    /// <summary>Applies build-only document directives. Enabled automatically by the project compiler.</summary>
    public bool IsPrecompilation { get; init; }
    public bool GenerateBuildMethod { get; init; } = true;
    public bool GenerateInitializeComponent { get; init; } = true;
    public bool GenerateNamedFields { get; init; } = true;
    public bool AdaptLoaderCalls { get; init; } = true;
    public XamlLoaderConfiguration? SourceLoader { get; init; }
    public string GeneratedNamespace { get; init; } = "XamlG.Generated";
    public string? DocumentId { get; init; }
    public string? BaseUri { get; init; }
    public bool EmitLineDirectives { get; init; } = true;
    public string? ResourceUri { get; init; }
    public IXamlResourceResolver? Resources { get; init; }
}
