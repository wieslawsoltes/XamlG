namespace XamlG.Runtime;

/// <summary>A generated instance, its logical construction parent and its compiler source mapping.</summary>
public sealed record XamlRuntimeNode(string Key, object Instance, string? ParentKey)
{
    public XamlSourceInfo? Source { get; init; }
}
