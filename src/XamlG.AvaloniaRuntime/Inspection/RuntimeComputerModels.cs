namespace XamlG.AvaloniaRuntime.Inspection;

public enum ComputerActionKind { Click, DoubleClick, Move, Down, Up, Scroll, Drag, Text, Key, Focus, Touch, Wait, Assert, Reset }
public enum ComputerCoordinateSpace { Image, Dips }
public sealed record ComputerTarget(string? ObjectId = null, string? Name = null, string? AutomationId = null, string? Text = null);
public sealed record ComputerPoint(double X, double Y);
public sealed record ComputerAction(ComputerActionKind Kind, ComputerTarget? Target = null, double? X = null, double? Y = null,
    string? Text = null, string? Key = null, string[]? Modifiers = null, string Button = "Left", double DeltaX = 0, double DeltaY = 0,
    ComputerPoint[]? Path = null, int Milliseconds = 0, RuntimeKeyAction KeyAction = RuntimeKeyAction.Press,
    long ContactId = 0, RuntimeTouchAction TouchAction = RuntimeTouchAction.Begin, bool? Enabled = null, bool? Visible = null);
public sealed record ComputerObserveOptions(bool Screenshot = true, int MaximumWidth = 1280, int MaximumHeight = 1024, int Offset = 0, int Count = 100);
public sealed record ComputerActionsRequest(string FrameId, long ExpectedRevision, ComputerAction[] Actions,
    ComputerCoordinateSpace CoordinateSpace = ComputerCoordinateSpace.Image, bool Screenshot = true);
public sealed record ComputerElement(string ObjectId, string Type, string? Name, string? AutomationId, string? Text,
    double X, double Y, double Width, double Height, bool Enabled, bool Visible, bool Focused);
public sealed record ComputerObservation(string SessionId, string FrameId, long Revision, string RootId,
    double Width, double Height, int ImageWidth, int ImageHeight, double ImageScale, string? FocusedObjectId,
    int Total, int Offset, bool HasMore, IReadOnlyList<ComputerElement> Elements);
public sealed record ComputerCapture(ComputerObservation Observation, byte[]? Png);
public sealed record ComputerActionResult(int Index, ComputerActionKind Kind, long Revision, string? ObjectId);
public sealed record ComputerActionsResult(ComputerCapture Capture, IReadOnlyList<ComputerActionResult> Completed, string? Error, int? FailedIndex);
