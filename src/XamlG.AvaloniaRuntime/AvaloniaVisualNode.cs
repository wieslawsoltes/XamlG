using System.Collections.Immutable;
using XamlG.Runtime;

namespace XamlG.AvaloniaRuntime;

public sealed record AvaloniaVisualNode(string Id, string TypeName, string? Name,
    double X, double Y, double Width, double Height, bool IsVisible, ImmutableArray<AvaloniaVisualNode> Children)
{
    public string? RuntimeKey { get; init; }
    public XamlSourceInfo? Source { get; init; }
    public bool IsSourceOwned { get; init; }
    public double RootX { get; init; }
    public double RootY { get; init; }
}
