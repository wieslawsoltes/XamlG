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

public sealed record RuntimeArgument(System.Text.Json.JsonElement? Value = null, string? ObjectId = null, string[]? Path = null);
public sealed record RuntimeMember(string Name, string Type, string Kind, bool ReadOnly, RuntimeValue? Value, string? Error = null);
public sealed record RuntimeObjectInspection(long Revision, RuntimeValue Value, int TotalMembers, int Offset, bool HasMore,
    IReadOnlyList<RuntimeMember> Members, IReadOnlyList<string> Methods);
public sealed record RuntimeType(string Name, string Assembly, string? BaseType, bool CanConstruct, bool IsControl, string? AssemblyQualifiedName);
public sealed record RuntimeFrameValue(string Property, RuntimeValue Value);
public sealed record RuntimeValueFrame(int Index, string Type, string Priority, bool Active, string SourceType, string? Description, IReadOnlyList<RuntimeFrameValue> Values);
public sealed record RuntimeBinding(string Property, string Type, string? Description, string? ErrorType, bool? IsRunning, string? Priority, RuntimeValue Value);
public sealed record RuntimeStyle(int Index, string Type, string? Selector, IReadOnlyList<RuntimeFrameValue> Setters);
public sealed record RuntimeSourceLocation(string? SourceUri, int LineNumber, int LinePosition);
public sealed record RuntimeSourceInspection(long Revision, RuntimeSourceLocation? FrameworkSource, XamlSourceInfo? Source, bool ResourceKey);
public enum RuntimeKeyAction { Press, Down, Up }
public enum RuntimePointerAction { Move, Down, Up, Click, DoubleClick, Wheel }
public enum RuntimeTouchAction { Begin, Move, End, Cancel }
public sealed record RuntimeInputResult(long Revision, bool Handled, string ObjectId);
public enum RuntimeAccessibilityAction { Focus, BringIntoView, ContextMenu }
public sealed record RuntimeAccessibilityNode(string Id, string? ObjectId, string? ParentId, IReadOnlyList<string> Children,
    IReadOnlyDictionary<string, RuntimeValue> Properties, IReadOnlyList<string> Providers, IReadOnlyDictionary<string, string> Errors);
public sealed record RuntimeAccessibilitySnapshot(string SessionId, long Revision, string RootId, int Total, int Offset, bool HasMore, IReadOnlyList<RuntimeAccessibilityNode> Nodes);
public sealed record RuntimeAccessibilityProvider(long Revision, string PeerId, string Provider, IReadOnlyList<RuntimeMember> Members, IReadOnlyList<string> Methods);
public sealed record RuntimeDictionaryEntry(RuntimeValue Key, RuntimeValue Value, string KeyType);
public sealed record RuntimeDictionarySnapshot(long Revision, string KeyType, string ValueType, bool ReadOnly, int Total, int Offset, bool HasMore, IReadOnlyList<RuntimeDictionaryEntry> Entries);
