using XamlG.Compiler.Resources;

namespace XamlG.Compiler;

public sealed record XamlCompilerOptions
{
    /// <summary>Maximum concurrent documents during project binding/emission. Defaults to one;
    /// hosts may opt in only when their framework rules and resource resolver support concurrent calls.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;
    /// <summary>Applies build-only document directives. Enabled automatically by the project compiler.</summary>
    public bool IsPrecompilation { get; init; }
    public bool GenerateBuildMethod { get; init; } = true;
    public bool GenerateInitializeComponent { get; init; } = true;
    public bool GenerateNamedFields { get; init; } = true;
    /// <summary>Share repeated scalar-parameter markup assignment bodies as ordinary typed methods.</summary>
    public bool ShareMarkupAssignments { get; init; } = true;
    public bool AdaptLoaderCalls { get; init; } = true;
    public XamlLoaderConfiguration? SourceLoader { get; init; }
    public string GeneratedNamespace { get; init; } = "XamlG.Generated";
    public string? DocumentId { get; init; }
    public string? BaseUri { get; init; }
    public bool EmitLineDirectives { get; init; } = true;
    public string? ResourceUri { get; init; }
    public IXamlResourceResolver? Resources { get; init; }
}
