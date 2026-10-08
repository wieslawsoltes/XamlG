using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using XamlG.Runtime;
using XamlG.Runtime.Design;

namespace XamlG.AvaloniaRuntime.Design;

/// <summary>Reusable selection, group movement and eight-handle resize surface.
/// Gestures draw an overlay; the host commits one source transaction.</summary>
public sealed class AvaloniaDesignerSurface : Panel
{
    private static readonly XamlResizeHandle[] Handles =
    [
        XamlResizeHandle.Left | XamlResizeHandle.Top, XamlResizeHandle.Top,
        XamlResizeHandle.Right | XamlResizeHandle.Top, XamlResizeHandle.Right,
        XamlResizeHandle.Right | XamlResizeHandle.Bottom, XamlResizeHandle.Bottom,
        XamlResizeHandle.Left | XamlResizeHandle.Bottom, XamlResizeHandle.Left
    ];
    private readonly AvaloniaDesignOverlay _overlay = new() { IsHitTestVisible = false };
    private Control? _content;
    private SelectionEntry[] _selection = [];
    private GestureEntry[] _gestureSelection = [];
    private XamlDesignGesture? _gesture;
    private Point _start;
    private XamlDesignRect _current;
    private IPointer? _pointer;
    private bool _designMode;
    private double _gridSize = 8;
    public AvaloniaDesignerSurface()
    {
        Focusable = true; Background = Brushes.Transparent; Children.Add(_overlay);
        AddHandler(PointerPressedEvent, Pressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        LayoutUpdated += (_, _) => RefreshSelection();
    }
    public event Action<XamlSourceInfo>? SourceSelected;
    public event Action<XamlVisualEdit>? EditRequested;
    public event Action<IReadOnlyList<XamlVisualEdit>>? EditsRequested;
    public event Action<Exception>? EditFailed;
    public event Action? DesignStateChanged;
    public IAvaloniaDesignerLayoutPolicy LayoutPolicy { get; set; } = new AvaloniaDesignerLayoutPolicy();
    public long DesignRevision { get; private set; }
    public bool IsGestureActive => _gesture != null;
    public IReadOnlyList<Control> SelectedControls => _selection.Select(entry => entry.Control).ToArray();
    public double GridSize
    {
        get => _gridSize;
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(value));
            if (_gridSize == value) return;
            CancelGesture(); _gridSize = value; Changed();
        }
    }
    public bool IsDesignMode
    {
        get => _designMode;
        set
        {
            if (_designMode == value) return;
            _designMode = value; CancelGesture();
            if (!value) SetSelection([], false);
            RefreshSelection(); Changed();
        }
    }
    public Control? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value)) return;
            CancelGesture(); SetSelection([], false);
            if (_content != null) Children.Remove(_content);
            _content = value;
            if (value != null) Children.Insert(0, value);
            Changed();
        }
    }
    public void SelectSource(int sourceStart) => SelectSource(null, sourceStart);
    public bool SelectSource(string? path, int sourceStart, long? version = null, bool notifySource = true)
    {
        if (_content == null) return false;
        var controls = _content.GetVisualDescendants().Prepend(_content).OfType<Control>().Take(10001).ToArray();
        if (controls.Length > 10000) throw new InvalidOperationException("The designer tree exceeds its selection limit.");
        var matches = controls.Select(control => (Control: control, Node: AvaloniaVisualInspector.FindSource(_content, control)))
            .Where(entry => ReferenceEquals(entry.Control, entry.Node?.Instance) && entry.Node?.Source is { } source &&
                source.Start == sourceStart && (path == null || source.Path == path) && (version == null || source.Version == version)).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Several realized visuals match this source. Select a concrete runtime object instead.");
        if (matches.Length == 0) return false;
        SelectControls([matches[0].Control], notifySource); return true;
    }
    public void SelectControls(IReadOnlyList<Control> controls, bool notifySource = true)
    {
        if (controls.Count > 256 || controls.Distinct(ReferenceEqualityComparer.Instance).Count() != controls.Count)
            throw new ArgumentException("Select at most 256 distinct controls.");
        var entries = controls.Select(control =>
        {
            if (_content == null || (!ReferenceEquals(control, _content) && !control.GetVisualAncestors().Contains(_content)))
                throw new ArgumentException("The selected control is outside this designer surface.");
            var node = AvaloniaVisualInspector.FindSource(_content, control);
            if (node?.Source == null || !ReferenceEquals(node.Instance, control)) throw new ArgumentException("Select a control with its own XAML source.");
            return new SelectionEntry(control, node);
        }).ToArray();
        if (entries.Any(entry => entry.Control.GetVisualAncestors().Any(parent => controls.Any(control => ReferenceEquals(control, parent)))))
            throw new ArgumentException("Do not select an ancestor and its descendant together.");
        if (entries.Select(entry => (entry.Node.Source!.Path, entry.Node.Source.Start, entry.Node.Source.Length)).Distinct().Count() != entries.Length)
            throw new ArgumentException("Selected instances share the same source element.");
        CancelGesture(); SetSelection(entries, notifySource);
    }
    public void CancelGesture()
    {
        var active = _gesture != null || _pointer != null;
        _gesture = null; _gestureSelection = []; _overlay.Ghost = null;
        var pointer = _pointer; _pointer = null; pointer?.Capture(null);
        _overlay.InvalidateVisual(); if (active) Changed();
    }
    private void SetSelection(SelectionEntry[] entries, bool notifySource)
    {
        if (_selection.Select(entry => entry.Control).SequenceEqual(entries.Select(entry => entry.Control))) return;
        _selection = entries; RefreshSelection(); Changed();
        if (notifySource && entries.LastOrDefault()?.Node.Source is { } source) SourceSelected?.Invoke(source);
    }
    private void RefreshSelection()
    {
        if (_selection.Any(entry => _content == null || (!ReferenceEquals(entry.Control, _content) && !entry.Control.GetVisualAncestors().Contains(_content))))
        { CancelGesture(); _selection = []; Changed(); }
        Rect? selection = null;
        if (IsDesignMode && _selection.Length != 0)
        {
            var union = XamlDesignGeometry.Union(_selection.Select(entry => BoundsInSurface(entry.Control)).ToArray());
            selection = new(union.X, union.Y, union.Width, union.Height);
        }
        if (_overlay.Selection != selection) { _overlay.Selection = selection; _overlay.InvalidateVisual(); }
    }
    private XamlDesignRect BoundsInSurface(Control control)
    {
        var origin = control.TranslatePoint(default, this) ?? throw new InvalidOperationException("The selected control has no designer coordinate space.");
        return new(origin.X, origin.Y, control.Bounds.Width, control.Bounds.Height);
    }
    private void Pressed(object? sender, PointerPressedEventArgs args)
    {
        if (!IsDesignMode || _content == null || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        args.Handled = true;
        try
        {
            var position = args.GetPosition(this); var handle = XamlResizeHandle.Move; var onHandle = false;
            if (_overlay.Selection is { } bounds)
                for (var i = 0; i < Handles.Length; i++)
                {
                    var point = AvaloniaDesignOverlay.Handles(bounds)[i];
                    if (Math.Abs(point.X - position.X) <= 7 && Math.Abs(point.Y - position.Y) <= 7)
                    { handle = Handles[i]; onHandle = true; break; }
                }
            if (!onHandle)
            {
                var local = this.TranslatePoint(position, _content) ?? default;
                var hit = _content.InputHitTest(local) as Visual;
                var node = hit == null ? null : AvaloniaVisualInspector.FindSource(_content, hit);
                var control = node?.Instance as Control;
                if (control == null) { SelectControls([]); return; }
                if ((args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)) != 0)
                {
                    var selected = _selection.Select(entry => entry.Control).ToList();
                    if (!selected.Remove(control)) selected.Add(control);
                    SelectControls(selected); return;
                }
                if (!_selection.Any(entry => ReferenceEquals(entry.Control, control))) SelectControls([control]);
            }
            if (_selection.Length == 0) return;
            Focus();
            _gestureSelection = _selection.Select(entry => new GestureEntry(entry.Control, entry.Node.Source!, BoundsInSurface(entry.Control))).ToArray();
            var initial = XamlDesignGeometry.Union(_gestureSelection.Select(entry => entry.Bounds).ToArray());
            var minWidth = Math.Max(1, _gestureSelection.Max(entry => entry.Bounds.Width == 0 ? 0 : initial.Width * entry.Control.MinWidth / entry.Bounds.Width));
            var minHeight = Math.Max(1, _gestureSelection.Max(entry => entry.Bounds.Height == 0 ? 0 : initial.Height * entry.Control.MinHeight / entry.Bounds.Height));
            _gesture = new(_gestureSelection[0].Source, initial, handle, minWidth, minHeight);
            _current = initial; _start = position; _pointer = args.Pointer; args.Pointer.Capture(this); Changed();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { CancelGesture(); EditFailed?.Invoke(error); }
    }
    private void Moved(object? sender, PointerEventArgs args)
    {
        if (_gesture == null) return;
        var point = args.GetPosition(this);
        _current = _gesture.Update(point.X - _start.X, point.Y - _start.Y, args.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0 : GridSize, args.KeyModifiers.HasFlag(KeyModifiers.Shift));
        _overlay.Ghost = new(_current.X, _current.Y, _current.Width, _current.Height);
        _overlay.InvalidateVisual(); args.Handled = true;
    }
    private void Released(object? sender, PointerReleasedEventArgs args)
    {
        if (_gesture == null) return;
        var gesture = _gesture; var current = _current; var selected = _gestureSelection;
        CancelGesture(); args.Handled = true;
        if (current == gesture.Initial) return;
        try
        {
            var after = XamlDesignGeometry.Transform(selected.Select(entry => entry.Bounds).ToArray(), gesture.Initial, current);
            PublishEdits(selected.Select((entry, index) => PlanEdit(entry, after[index], gesture.Handle)).ToArray());
        }
        catch (Exception error) when (error is not OutOfMemoryException) { EditFailed?.Invoke(error); }
    }
    private void KeyPressed(object? sender, KeyEventArgs args)
    {
        if (!IsDesignMode) return;
        if (args.Key == Key.Escape) { CancelGesture(); args.Handled = true; return; }
        if (_selection.Length == 0 || args.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var step = args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1; args.Handled = true;
        try
        {
            PublishEdits(_selection.Select(entry =>
            {
                var before = BoundsInSurface(entry.Control);
                var after = before with { X = before.X + (args.Key == Key.Left ? -step : args.Key == Key.Right ? step : 0),
                    Y = before.Y + (args.Key == Key.Up ? -step : args.Key == Key.Down ? step : 0) };
                return PlanEdit(new(entry.Control, entry.Node.Source!, before), after, XamlResizeHandle.Move);
            }).ToArray());
        }
        catch (Exception error) when (error is not OutOfMemoryException) { EditFailed?.Invoke(error); }
    }
    private XamlVisualEdit PlanEdit(GestureEntry entry, XamlDesignRect after, XamlResizeHandle handle)
    {
        ValidateGestureEntry(entry);
        if (after.Width < entry.Control.MinWidth || after.Height < entry.Control.MinHeight || after.Width > entry.Control.MaxWidth || after.Height > entry.Control.MaxHeight)
            throw new InvalidOperationException("The group resize exceeds a selected control's size constraints.");
        var properties = LayoutPolicy.GetPropertyEdits(entry.Control, entry.Bounds, after, handle);
        ValidateGestureEntry(entry);
        return new(entry.Source, properties);
    }
    private void ValidateGestureEntry(GestureEntry entry)
    {
        if (_content == null || (!ReferenceEquals(entry.Control, _content) && !entry.Control.GetVisualAncestors().Contains(_content)) ||
            !entry.Control.IsMeasureValid || !entry.Control.IsArrangeValid || BoundsInSurface(entry.Control) != entry.Bounds ||
            !ReferenceEquals(AvaloniaVisualInspector.FindSource(_content, entry.Control)?.Source, entry.Source))
            throw new InvalidOperationException("The selected visual's layout or source changed during the gesture. Start the gesture again.");
    }
    private void PublishEdits(IReadOnlyList<XamlVisualEdit> edits)
    {
        if (EditsRequested != null) EditsRequested(edits);
        else if (edits.Count == 1) EditRequested?.Invoke(edits[0]);
        else throw new InvalidOperationException("The host must subscribe to EditsRequested to commit group gestures atomically.");
    }
    private void Changed() { DesignRevision++; DesignStateChanged?.Invoke(); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args) { base.OnPointerCaptureLost(args); CancelGesture(); }
    private sealed record SelectionEntry(Control Control, XamlRuntimeNode Node);
    private sealed record GestureEntry(Control Control, XamlSourceInfo Source, XamlDesignRect Bounds);
}
