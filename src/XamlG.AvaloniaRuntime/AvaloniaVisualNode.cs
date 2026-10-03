using System.Collections.Immutable;

namespace XamlG.AvaloniaRuntime;

public sealed record AvaloniaVisualNode(string Id, string TypeName, string? Name,
    double X, double Y, double Width, double Height, bool IsVisible, ImmutableArray<AvaloniaVisualNode> Children);
