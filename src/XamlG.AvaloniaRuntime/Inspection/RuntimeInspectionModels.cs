using XamlG.Runtime;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed record RuntimeValue(string Type, object? Value, string? ObjectId = null, bool Truncated = false);

public sealed record RuntimeProperty(string Key, string Name, string Owner, string Type, string Kind,
    bool ReadOnly, RuntimeValue? Value, bool IsSet = false, bool IsAnimating = false,
    string? Priority = null, string? Error = null);

public sealed record RuntimeNode(string Id, string Type, string? Name, string? VisualParent,
    string? LogicalParent, IReadOnlyList<string> VisualChildren, IReadOnlyList<string> LogicalChildren,
    IReadOnlyList<string> Classes, RuntimeValue? DataContext, RuntimeBounds? Bounds,
    XamlSourceInfo? Source, bool IsSourceOwned);

public sealed record RuntimeBounds(double X, double Y, double Width, double Height,
    double RootX, double RootY, bool IsVisible);

public sealed record RuntimeSnapshot(string SessionId, long Revision, string RootId,
    IReadOnlyList<RuntimeNode> Nodes);

public sealed record RuntimeChange(long Sequence, long Revision, string ObjectId, string Kind,
    string Name, RuntimeValue? Value);

public sealed record RuntimeChanges(long Sequence, bool HistoryLost, IReadOnlyList<RuntimeChange> Changes);
