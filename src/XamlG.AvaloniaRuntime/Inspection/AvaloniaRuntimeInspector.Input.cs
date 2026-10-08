using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Pointer = Avalonia.Input.Pointer;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed partial class AvaloniaRuntimeInspector
{
    private MouseDevice? _inputMouse;
    private Pointer? _inputPointer;
    private TouchDevice? _inputTouch;
    private TopLevel? _inputTopLevel;
    private RawInputModifiers _inputButtons;
    private readonly HashSet<long> _touchContacts = [];

    public RuntimeInputResult SendKey(string objectId, string key, RuntimeKeyAction action, long expectedRevision,
        IReadOnlyList<string>? modifiers = null, string? physicalKey = null, string? symbol = null)
    {
        var parsedKey = InputEnum<Key>(key);
        var physical = physicalKey == null ? PhysicalKey.None : InputEnum<PhysicalKey>(physicalKey);
        if (!Enum.IsDefined(action) || symbol?.Length > 32) throw new ArgumentException("Invalid key action or symbol.");
        var flags = InputModifiers(modifiers);
        var target = InputTarget(objectId, expectedRevision);
        var (root, send) = InputRoute(target);
        var keyboard = AvaloniaLocator.Current.GetService<IKeyboardDevice>() ?? throw new InvalidOperationException("No keyboard device is available.");
        if (!target.Focus() || !ReferenceEquals(_inputTopLevel?.FocusManager.GetFocusedElement(), target)) throw new InvalidOperationException("The target did not accept keyboard focus.");
        // Focus callbacks can remove or replace the target. Never dispatch into another preview.
        if (!ReferenceEquals(Resolve(objectId), target) || !ReferenceEquals(TopLevel.GetTopLevel(target), _inputTopLevel))
            throw new InvalidOperationException("The target detached while receiving focus.");
        var handled = false;
        void Dispatch(RawKeyEventType type)
        {
            var args = new RawKeyEventArgs(keyboard, InputTimestamp(), root, type, parsedKey, flags, physical, symbol);
            send(args); handled |= args.Handled;
        }
        if (action == RuntimeKeyAction.Press)
        {
            try { Dispatch(RawKeyEventType.KeyDown); }
            finally
            {
                if (ReferenceEquals(_inputTopLevel?.FocusManager.GetFocusedElement(), target) && ReferenceEquals(TopLevel.GetTopLevel(target), _inputTopLevel)) Dispatch(RawKeyEventType.KeyUp);
            }
        }
        else Dispatch(action == RuntimeKeyAction.Down ? RawKeyEventType.KeyDown : RawKeyEventType.KeyUp);
        Changed(objectId, "input", "key:" + key, null);
        return new(Revision, handled, objectId);
    }

    public RuntimeInputResult SendText(string objectId, string text, long expectedRevision)
    {
        if (text.Length is < 1 or > 16384) throw new ArgumentException("Text input must contain 1–16,384 characters.");
        var target = InputTarget(objectId, expectedRevision);
        var (root, send) = InputRoute(target);
        var keyboard = AvaloniaLocator.Current.GetService<IKeyboardDevice>() ?? throw new InvalidOperationException("No keyboard device is available.");
        if (!target.Focus() || !ReferenceEquals(_inputTopLevel?.FocusManager.GetFocusedElement(), target)) throw new InvalidOperationException("The target did not accept keyboard focus.");
        if (!ReferenceEquals(Resolve(objectId), target) || !ReferenceEquals(TopLevel.GetTopLevel(target), _inputTopLevel))
            throw new InvalidOperationException("The target detached while receiving focus.");
        var args = new RawTextInputEventArgs(keyboard, InputTimestamp(), root, text); send(args);
        Changed(objectId, "input", "text", null);
        return new(Revision, args.Handled, objectId);
    }

    public RuntimeInputResult SendPointer(string objectId, RuntimePointerAction action, long expectedRevision,
        double? x = null, double? y = null, string button = "Left", IReadOnlyList<string>? modifiers = null,
        double deltaX = 0, double deltaY = 0)
    {
        if (!Enum.IsDefined(action) || !double.IsFinite(deltaX) || !double.IsFinite(deltaY) || Math.Abs(deltaX) > 10000 || Math.Abs(deltaY) > 10000)
            throw new ArgumentException("Invalid pointer action or wheel delta.");
        var (down, up, pressed) = button.ToLowerInvariant() switch
        {
            "left" => (RawPointerEventType.LeftButtonDown, RawPointerEventType.LeftButtonUp, RawInputModifiers.LeftMouseButton),
            "right" => (RawPointerEventType.RightButtonDown, RawPointerEventType.RightButtonUp, RawInputModifiers.RightMouseButton),
            "middle" => (RawPointerEventType.MiddleButtonDown, RawPointerEventType.MiddleButtonUp, RawInputModifiers.MiddleMouseButton),
            "xbutton1" => (RawPointerEventType.XButton1Down, RawPointerEventType.XButton1Up, RawInputModifiers.XButton1MouseButton),
            "xbutton2" => (RawPointerEventType.XButton2Down, RawPointerEventType.XButton2Up, RawInputModifiers.XButton2MouseButton),
            _ => throw new ArgumentException("Use Left, Right, Middle, XButton1 or XButton2.")
        };
        var flags = InputModifiers(modifiers);
        var target = InputTarget(objectId, expectedRevision); var (root, send) = InputRoute(target);
        var point = InputPoint(target, root, x, y, _inputPointer?.Captured);
        _inputPointer ??= new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        _inputMouse ??= new(_inputPointer);
        var handled = false;
        void Dispatch(RawPointerEventType type)
        {
            if (_inputPointer == null || _inputMouse == null || !ReferenceEquals(TopLevel.GetTopLevel(target), _inputTopLevel) ||
                (_inputPointer.Captured is Visual capture && !WithinRoot(capture)) ||
                (_inputPointer.Captured == null && InputRootElement(root).InputHitTest(point) is Visual hit && !WithinRoot(hit)))
            { DisposeInput(); throw new InvalidOperationException("The pointer target detached or moved outside the inspected tree."); }
            RawPointerEventArgs args = type == RawPointerEventType.Wheel
                ? new RawMouseWheelEventArgs(_inputMouse, InputTimestamp(), root, point, new(deltaX, deltaY), flags | _inputButtons)
                : new RawPointerEventArgs(_inputMouse, InputTimestamp(), root, type, point, flags | _inputButtons);
            send(args); handled |= args.Handled;
        }
        void Press() { _inputButtons |= pressed; Dispatch(down); }
        void Release() { _inputButtons &= ~pressed; Dispatch(up); }
        switch (action)
        {
            case RuntimePointerAction.Down:
                if ((_inputButtons & pressed) != 0) throw new InvalidOperationException("That pointer button is already pressed.");
                Press(); break;
            case RuntimePointerAction.Up:
                if ((_inputButtons & pressed) == 0) throw new InvalidOperationException("That pointer button is not pressed.");
                Release(); break;
            case RuntimePointerAction.Click:
            case RuntimePointerAction.DoubleClick:
                if (_inputButtons != RawInputModifiers.None) throw new InvalidOperationException("Release held pointer buttons before clicking.");
                for (var click = 0; click < (action == RuntimePointerAction.DoubleClick ? 2 : 1); click++)
                { try { Press(); } finally { Release(); } }
                break;
            case RuntimePointerAction.Move: Dispatch(RawPointerEventType.Move); break;
            case RuntimePointerAction.Wheel: Dispatch(RawPointerEventType.Wheel); break;
        }
        Changed(objectId, "input", "pointer:" + action, null);
        return new(Revision, handled, objectId);
    }

    public RuntimeInputResult SendTouch(string objectId, long contactId, RuntimeTouchAction action, double x, double y,
        long expectedRevision, IReadOnlyList<string>? modifiers = null)
    {
        if (contactId is < 1 or > 1000000 || !Enum.IsDefined(action)) throw new ArgumentException("Invalid touch contact or action.");
        if (action == RuntimeTouchAction.Begin ? _touchContacts.Contains(contactId) || _touchContacts.Count >= 16 : !_touchContacts.Contains(contactId))
            throw new InvalidOperationException("Begin a new contact (at most 16), or update/end an existing one.");
        var flags = InputModifiers(modifiers); var target = InputTarget(objectId, expectedRevision); var (root, send) = InputRoute(target);
        _inputTouch ??= new();
        var type = action switch { RuntimeTouchAction.Begin => RawPointerEventType.TouchBegin, RuntimeTouchAction.Move => RawPointerEventType.TouchUpdate,
            RuntimeTouchAction.End => RawPointerEventType.TouchEnd, _ => RawPointerEventType.TouchCancel };
        var args = new RawPointerEventArgs(_inputTouch, InputTimestamp(), root, type, default(Point), flags) { RawPointerId = contactId };
        // Capture belongs to this contact. Mouse capture must not authorize a new
        // touch outside the preview, and captured touches may cross its bounds.
        args.Position = InputPoint(target, root, x, y, _inputTouch.TryGetPointer(args)?.Captured);
        if (action == RuntimeTouchAction.Begin) _touchContacts.Add(contactId);
        if (action is RuntimeTouchAction.End or RuntimeTouchAction.Cancel) _touchContacts.Remove(contactId);
        try { send(args); }
        catch { DisposeInput(); throw; }
        Changed(objectId, "input", "touch:" + action, null);
        return new(Revision, args.Handled, objectId);
    }

    public void ResetInput(long expectedRevision)
    {
        Capture(); if (Revision != expectedRevision) throw new InvalidOperationException("Runtime revision changed. Inspect again before resetting input.");
        DisposeInput(); Changed(Id(_root), "input", "reset", null);
    }

    private Control InputTarget(string id, long revision)
    {
        var target = ResolveForMutation(id, revision) as Control ?? throw new ArgumentException("Input requires a live control.");
        if (!target.IsEffectivelyVisible || !target.IsEffectivelyEnabled) throw new InvalidOperationException("The control is not visible and enabled.");
        return target;
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(TopLevel))]
    private (IInputRoot Root, Action<RawInputEventArgs> Send) InputRoute(Control control)
    {
        var topLevel = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("The control is not attached to a running top level.");
        if (_inputTopLevel != null && !ReferenceEquals(_inputTopLevel, topLevel))
        { DisposeInput(); throw new InvalidOperationException("The inspected tree moved to another top level. Input was reset; inspect before retrying."); }
        // Avalonia 12 moved IInputRoot from TopLevel to its presentation source.
        // Keep this version-specific access in one place; dispatch still uses the
        // actual platform input callback, including framework hit testing.
        var root = typeof(TopLevel).GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(topLevel) as IInputRoot
            ?? throw new NotSupportedException("The loaded Avalonia version does not expose the expected input root.");
        var send = topLevel.PlatformImpl?.Input ?? throw new InvalidOperationException("The top level has no active input route.");
        _inputTopLevel = topLevel;
        return (root, send);
    }
    private Point InputPoint(Control target, IInputRoot inputRoot, double? x, double? y, IInputElement? captured)
    {
        var local = new Point(x ?? target.Bounds.Width / 2, y ?? target.Bounds.Height / 2);
        if (!double.IsFinite(local.X) || !double.IsFinite(local.Y) || Math.Abs(local.X) > 1000000 || Math.Abs(local.Y) > 1000000)
            throw new ArgumentException("Input coordinates must be finite and bounded.");
        var point = target.TranslatePoint(local, InputRootElement(inputRoot)) ?? throw new InvalidOperationException("The input target detached.");
        if (captured is Visual capture && !WithinRoot(capture))
        { DisposeInput(); throw new InvalidOperationException("The input capture moved outside the inspected tree. Input was reset; inspect before retrying."); }
        if (captured == null && InputRootElement(inputRoot).InputHitTest(point) is Visual hit && !WithinRoot(hit))
            throw new InvalidOperationException("This position hits outside the inspected tree. Leave design mode or choose a visible target.");
        return point;
    }
    private bool WithinRoot(Visual visual) => ReferenceEquals(visual, _root) ||
        (_objects.TryGetValue(Id(visual), out var owned) && ReferenceEquals(visual, owned)) ||
        visual.GetVisualAncestors().Any(parent => ReferenceEquals(parent, _root));
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.PublicProperties, typeof(IInputRoot))]
    private static InputElement InputRootElement(IInputRoot root) =>
        typeof(IInputRoot).GetProperty("RootElement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(root) as InputElement
        ?? throw new NotSupportedException("The loaded Avalonia version does not expose the expected input root element.");
    private static ulong InputTimestamp() => (ulong)Environment.TickCount64;
    private static T InputEnum<T>(string name) where T : struct, Enum => name.Length is > 0 and <= 64 && char.IsLetter(name[0]) &&
        Enum.TryParse<T>(name, true, out var value) && Enum.IsDefined(value) ? value : throw new ArgumentException("Unknown " + typeof(T).Name + ": " + name);
    private static RawInputModifiers InputModifiers(IReadOnlyList<string>? modifiers)
    {
        if (modifiers?.Count > 4) throw new ArgumentException("Use at most four keyboard modifiers.");
        var flags = RawInputModifiers.None;
        foreach (var modifier in modifiers ?? []) flags |= modifier.ToLowerInvariant() switch
        { "shift" => RawInputModifiers.Shift, "control" or "ctrl" => RawInputModifiers.Control, "alt" => RawInputModifiers.Alt,
            "meta" or "command" => RawInputModifiers.Meta, _ => throw new ArgumentException("Unknown keyboard modifier: " + modifier) };
        return flags;
    }
    private void DisposeInput()
    {
        var mouse = _inputMouse; var touch = _inputTouch;
        _inputMouse = null; _inputPointer = null; _inputTouch = null;
        _inputTopLevel = null; _inputButtons = RawInputModifiers.None; _touchContacts.Clear();
        try { mouse?.Dispose(); } finally { touch?.Dispose(); }
    }
}
