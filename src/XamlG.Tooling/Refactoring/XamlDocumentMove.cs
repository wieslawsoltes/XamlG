namespace XamlG.Tooling.Refactoring;

/// <summary>A host-resolved physical and logical move. ResourceUri is optional: explicit existing
/// export addresses are retained unless the host supplies a replacement. No filesystem operation is performed.</summary>
public sealed record XamlDocumentMove(string OldPath, string NewPath, string NewLogicalPath)
{
    public string? NewResourceUri { get; init; }
}
