using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using XamlG.Runtime;
using XamlG.Runtime.Design;

namespace XamlG.AvaloniaRuntime.Design;

/// <summary>Reusable selection/drag/eight-handle resize surface. Gestures only draw an overlay;
/// a host commits the resulting source transaction, keeping runtime and source ownership separate.</summary>
public sealed class AvaloniaDesignerSurface : Panel
{
    private static readonly XamlResizeHandle[] Handles =
    {
        XamlResizeHandle.Left | XamlResizeHandle.Top, XamlResizeHandle.Top,
        XamlResizeHandle.Right | XamlResizeHandle.Top, XamlResizeHandle.Right,
        XamlResizeHandle.Right | XamlResizeHandle.Bottom, XamlResizeHandle.Bottom,
        XamlResizeHandle.Left | XamlResizeHandle.Bottom, XamlResizeHandle.Left
    };
    private readonly AvaloniaDesignOverlay _overlay = new() { IsHitTestVisible = false };
    private Control? _content;
    private Control? _selected;
    private XamlRuntimeNode? _source;
    private XamlDesignGesture? _gesture;
    private Point _start;
    private XamlDesignRect _current;
    private IPointer? _pointer;
    private bool _designMode;
    public AvaloniaDesignerSurface()
    {
        Focusable = true;
        Children.Add(_overlay);
        AddHandler(PointerPressedEvent, Pressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    public event Action<XamlSourceInfo>? SourceSelected;
    public event Action<XamlVisualEdit>? EditRequested;
    public IAvaloniaDesignerLayoutPolicy LayoutPolicy { get; set; } = new AvaloniaDesignerLayoutPolicy();
    public double GridSize { get; set; } = 8;
    public bool IsDesignMode
    {
        get => _designMode;
        set { _designMode = value; CancelGesture(); if (!value) Select(null, null); }
    }
    public Control? Content
    {
        get => _content;
        set
        {
            CancelGesture(); Select(null, null);
            if (_content != null) Children.Remove(_content);
            _content = value;
            if (value != null) Children.Insert(0, value);
        }
    }
    public void SelectSource(int sourceStart)
    {
        if (_content == null || !XamlRuntimeSession.TryGet(_content, out var session)) return;
        var node = session!.Nodes.FirstOrDefault(n => n.Source?.Start == sourceStart && n.Instance is Control);
        if (node?.Instance is Control control) Select(control, node);
    }

    private void Select(Control? control, XamlRuntimeNode? source)
    {
        _selected = control; _source = source;
        _overlay.Selection = control == null ? null : BoundsInSurface(control);
        _overlay.InvalidateVisual();
        if (source?.Source != null) SourceSelected?.Invoke(source.Source);
    }
    private Rect BoundsInSurface(Control control)
    {
        var origin = control.TranslatePoint(default, this) ?? default;
        return new(origin, control.Bounds.Size);
    }
    private void Pressed(object? sender, PointerPressedEventArgs args)
    {
        if (!IsDesignMode || _content == null || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var position = args.GetPosition(this);
        var handle = XamlResizeHandle.Move;
        var onHandle = false;
        if (_selected != null && _overlay.Selection is { } bounds)
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
            Select(node?.Instance as Control, node);
        }
        args.Handled = true;
        if (_selected == null || _source?.Source == null) return;
        Focus();
        var currentBounds = BoundsInSurface(_selected);
        _gesture = new(_source.Source, new(currentBounds.X, currentBounds.Y, currentBounds.Width, currentBounds.Height), handle,
            Math.Max(1, _selected.MinWidth), Math.Max(1, _selected.MinHeight));
        _current = _gesture.Initial; _start = position;
        _pointer = args.Pointer; args.Pointer.Capture(this);
    }
    private void Moved(object? sender, PointerEventArgs args)
    {
        if (_gesture == null) return;
        var point = args.GetPosition(this);
        _current = _gesture.Update(point.X - _start.X, point.Y - _start.Y,
            args.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0 : GridSize,
            args.KeyModifiers.HasFlag(KeyModifiers.Shift));
        _overlay.Ghost = new(_current.X, _current.Y, _current.Width, _current.Height);
        _overlay.InvalidateVisual(); args.Handled = true;
    }
    private void Released(object? sender, PointerReleasedEventArgs args)
    {
        if (_gesture == null || _selected == null) return;
        var gesture = _gesture; var current = _current; var selected = _selected;
        CancelGesture(); args.Handled = true;
        if (current == gesture.Initial) return;
        var changes = LayoutPolicy.GetPropertyEdits(selected, gesture.Initial, current, gesture.Handle);
        if (changes.Count != 0) EditRequested?.Invoke(new(gesture.Source, changes));
    }
    private void KeyPressed(object? sender, KeyEventArgs args)
    {
        if (!IsDesignMode) return;
        if (args.Key == Key.Escape) { CancelGesture(); args.Handled = true; return; }
        if (_selected == null || _source?.Source == null || args.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var step = args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var bounds = BoundsInSurface(_selected);
        var before = new XamlDesignRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var after = before with
        {
            X = before.X + (args.Key == Key.Left ? -step : args.Key == Key.Right ? step : 0),
            Y = before.Y + (args.Key == Key.Up ? -step : args.Key == Key.Down ? step : 0)
        };
        args.Handled = true;
        EditRequested?.Invoke(new(_source.Source, LayoutPolicy.GetPropertyEdits(_selected, before, after, XamlResizeHandle.Move)));
    }
    private void CancelGesture()
    {
        _gesture = null; _overlay.Ghost = null;
        var pointer = _pointer; _pointer = null; pointer?.Capture(null);
        _overlay.InvalidateVisual();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args)
    {
        base.OnPointerCaptureLost(args); CancelGesture();
    }
}
