using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed partial class AvaloniaRuntimeInspector
{
    private readonly Dictionary<string, ComputerFrame> _computerFrames = new(StringComparer.Ordinal);
    private sealed record ComputerFrame(ComputerObservation Observation, string Topology, Dictionary<string, ComputerTargetState[]> Targets);
    private sealed record ComputerTargetState(Control Control, object? DataContext, string? Name, string? AutomationId, string? Text,
        bool Enabled, bool Visible, bool HitTestVisible, bool Focusable, double Opacity)
    {
        public bool Matches(ComputerTargetState other) => ReferenceEquals(Control, other.Control) && ReferenceEquals(DataContext, other.DataContext) &&
            Name == other.Name && AutomationId == other.AutomationId && Text == other.Text && Enabled == other.Enabled && Visible == other.Visible &&
            HitTestVisible == other.HitTestVisible && Focusable == other.Focusable && Opacity == other.Opacity;
    }
    private TopLevel ComputerTopLevel => _root is Control root ? TopLevel.GetTopLevel(root) ?? throw new InvalidOperationException("The preview is detached.")
        : throw new InvalidOperationException("Computer interaction requires a control root.");

    public ComputerCapture ObserveComputer(ComputerObserveOptions options)
    {
        VerifyAccess();
        if (options.MaximumWidth is < 64 or > 2048 || options.MaximumHeight is < 64 or > 2048 || options.Offset < 0 || options.Count is < 1 or > 200)
            throw new ArgumentException("Use image dimensions 64–2048 and a bounded element page of 1–200.");
        var top = ComputerTopLevel; top.UpdateLayout();
        var tree = Capture();
        var width = top.Bounds.Width; var height = top.Bounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) throw new InvalidOperationException("The preview has no visible viewport.");
        var scale = Math.Min(1, Math.Min(options.MaximumWidth / width, options.MaximumHeight / height));
        var imageWidth = Math.Max(1, (int)Math.Ceiling(width * scale)); var imageHeight = Math.Max(1, (int)Math.Ceiling(height * scale));
        var focused = top.FocusManager.GetFocusedElement() as Control;
        var controls = _objects.Values.OfType<Control>().ToArray();
        var page = controls.Skip(options.Offset).Take(options.Count).ToArray();
        var elements = page.Select(control =>
        {
            var point = control.TranslatePoint(default, top) ?? default;
            var text = ComputerText(control);
            return new ComputerElement(Id(control), control.GetType().Name, control.Name, AutomationProperties.GetAutomationId(control),
                text?.Length > 300 ? text[..300] + "…" : text, point.X, point.Y, control.Bounds.Width, control.Bounds.Height,
                control.IsEffectivelyEnabled, control.IsEffectivelyVisible, ReferenceEquals(control, focused));
        }).ToArray();
        byte[]? png = null;
        if (options.Screenshot)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(imageWidth, imageHeight), new Vector(96 * scale, 96 * scale));
            bitmap.Render(top);
            using var output = new MemoryStream(); bitmap.Save(output, PngBitmapEncoderOptions.Default); png = output.ToArray();
            if (png.Length > 2_097_152) throw new InvalidOperationException("Screenshot exceeds 2 MiB. Request smaller dimensions.");
        }
        var frame = new ComputerObservation(SessionId, Guid.NewGuid().ToString("N"), Revision, tree.RootId, width, height, imageWidth, imageHeight, scale,
            focused != null && WithinRoot(focused) ? Id(focused) : null, controls.Length, options.Offset, options.Offset + elements.Length < controls.Length, elements);
        while (_computerFrames.Count >= 4) _computerFrames.Remove(_computerFrames.Keys.First());
        _computerFrames.Add(frame.FrameId, new(frame, _topology, page.ToDictionary(Id, ComputerTargetStates, StringComparer.Ordinal)));
        return new(frame, png);
    }

    public async Task<ComputerActionsResult> ComputerActionsAsync(ComputerActionsRequest request, CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        if (!_computerFrames.TryGetValue(request.FrameId, out var stored) || stored.Observation.SessionId != SessionId)
            throw new InvalidOperationException("The observed frame expired. Observe the current preview first.");
        var frame = stored.Observation;
        Capture();
        if ((!request.RefreshTargets && Revision != request.ExpectedRevision) || frame.Revision != request.ExpectedRevision || ComputerTopLevel.Bounds.Width != frame.Width || ComputerTopLevel.Bounds.Height != frame.Height)
            throw new InvalidOperationException("The preview changed since observation. Observe again before acting.");
        if (request.Actions.Length is < 1 or > 32 || !Enum.IsDefined(request.CoordinateSpace) ||
            request.Actions.Any(action => !Enum.IsDefined(action.Kind) || action.Milliseconds is < 0 or > 1000 || action.Path?.Length > 64 || action.Text?.Length > 16384) ||
            request.Actions.Sum(action => action.Milliseconds) > 10000)
            throw new ArgumentException("Use 1–32 actions, at most 64 drag points and bounded waits totaling at most 10 seconds.");
        // Resolve and validate the entire batch before input. Keep those identities
        // even if an earlier action replaces a later named control.
        var refreshedTargets = request.RefreshTargets ? RefreshComputerTargets(stored, request.Actions) : null;
        var completed = new List<ComputerActionResult>(); string? error = null; int? failed = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); lifetime.CancelAfter(TimeSpan.FromSeconds(20));
        for (var index = 0; index < request.Actions.Length; index++)
        {
            var action = request.Actions[index];
            try
            {
                lifetime.Token.ThrowIfCancellationRequested(); Capture();
                if (action.Kind == ComputerActionKind.Wait)
                { await Task.Delay(action.Milliseconds, lifetime.Token); completed.Add(new(index, action.Kind, Revision, null)); continue; }
                if (action.Kind == ComputerActionKind.Reset)
                { ResetInput(Revision); completed.Add(new(index, action.Kind, Revision, null)); continue; }
                var target = refreshedTargets == null ? ComputerTargetControl(action.Target, action.Kind is ComputerActionKind.Key or ComputerActionKind.Text)
                    : Resolve(Id(refreshedTargets[index]!)) as Control ?? throw new InvalidOperationException("The observed target detached. Observe again before acting.");
                var id = Id(target);
                var point = PointFor(action.X, action.Y, target);
                switch (action.Kind)
                {
                    case ComputerActionKind.Assert:
                        if (action.Text != null && ComputerText(target) != action.Text || action.Enabled is { } enabled && target.IsEffectivelyEnabled != enabled ||
                            action.Visible is { } visible && target.IsEffectivelyVisible != visible) throw new InvalidOperationException("Preview assertion did not match the current control.");
                        break;
                    case ComputerActionKind.Focus: if (!Focus(id, Revision)) throw new InvalidOperationException("The control rejected focus."); break;
                    case ComputerActionKind.Text: SendText(id, action.Text ?? throw new ArgumentException("Text is required."), Revision); break;
                    case ComputerActionKind.Key: SendKey(id, action.Key ?? throw new ArgumentException("Key is required."), action.KeyAction, Revision, action.Modifiers); break;
                    case ComputerActionKind.Touch: SendTouch(id, action.ContactId, action.TouchAction, point.X, point.Y, Revision, action.Modifiers, requireTargetHit: request.RefreshTargets); break;
                    case ComputerActionKind.Drag:
                        if (action.Path is not { Length: > 0 } path) throw new ArgumentException("A drag needs at least one path point.");
                        SendPointer(id, RuntimePointerAction.Down, Revision, point.X, point.Y, action.Button, action.Modifiers);
                        try
                        {
                            foreach (var next in path)
                            {
                                var local = PointFor(next.X, next.Y, target);
                                await Task.Delay(16, lifetime.Token); Capture();
                                SendPointer(id, RuntimePointerAction.Move, Revision, local.X, local.Y, action.Button, action.Modifiers); point = local;
                            }
                        }
                        finally { Capture(); SendPointer(id, RuntimePointerAction.Up, Revision, point.X, point.Y, action.Button, action.Modifiers); }
                        break;
                    default:
                        var pointerAction = action.Kind switch
                        {
                            ComputerActionKind.Click => RuntimePointerAction.Click, ComputerActionKind.DoubleClick => RuntimePointerAction.DoubleClick,
                            ComputerActionKind.Move => RuntimePointerAction.Move, ComputerActionKind.Down => RuntimePointerAction.Down,
                            ComputerActionKind.Up => RuntimePointerAction.Up, ComputerActionKind.Scroll => RuntimePointerAction.Wheel,
                            _ => throw new ArgumentException("Unknown computer action.")
                        };
                        SendPointer(id, pointerAction, Revision, point.X, point.Y, action.Button, action.Modifiers, action.DeltaX, action.DeltaY, requireTargetHit: request.RefreshTargets); break;
                }
                await Dispatcher.UIThread.InvokeAsync(() => ComputerTopLevel.UpdateLayout(), DispatcherPriority.Loaded, lifetime.Token);
                completed.Add(new(index, action.Kind, Revision, id));
                if (action.Milliseconds != 0) await Task.Delay(action.Milliseconds, lifetime.Token);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                DisposeInput(); error = failure is OperationCanceledException ? "The action sequence was cancelled. Inspect completed actions before retrying." : failure.Message;
                failed = index; break;
            }
        }
        return new(ObserveComputer(new(Screenshot: request.Screenshot, MaximumWidth: frame.ImageWidth < 64 ? 64 : frame.ImageWidth, MaximumHeight: frame.ImageHeight < 64 ? 64 : frame.ImageHeight)), completed, error, failed);

        Point PointFor(double? x, double? y, Control target)
        {
            if (x == null && y == null) return new(target.Bounds.Width / 2, target.Bounds.Height / 2);
            if (x == null || y == null || !double.IsFinite(x.Value) || !double.IsFinite(y.Value)) throw new ArgumentException("Provide both finite coordinates.");
            var factor = request.CoordinateSpace == ComputerCoordinateSpace.Image ? frame.ImageScale : 1;
            var point = new Point(x.Value / factor, y.Value / factor);
            if (point.X < 0 || point.Y < 0 || point.X >= frame.Width || point.Y >= frame.Height) throw new ArgumentException("Coordinates are outside the observed preview viewport.");
            return ComputerTopLevel.TranslatePoint(point, target) ?? throw new InvalidOperationException("Target detached from the observed viewport.");
        }
    }

    private Control?[] RefreshComputerTargets(ComputerFrame frame, ComputerAction[] actions)
    {
        if (frame.Topology != _topology) throw new InvalidOperationException("The preview tree changed. Observe again before refreshing targets.");
        if (actions[0].Kind != ComputerActionKind.Reset && (_inputPointer?.Captured != null || _inputButtons != 0 || _touchContacts.Count != 0))
            throw new InvalidOperationException("Reset held input before refreshing targets, or start the batch with reset.");
        var targets = new Control?[actions.Length];
        for (var index = 0; index < actions.Length; index++)
        {
            var action = actions[index];
            if (action.X != null || action.Y != null || action.Path != null || action.Kind == ComputerActionKind.Drag)
                throw new ArgumentException("Refreshing targets requires selectors without coordinates or drag paths. Observe a fresh frame for coordinates.");
            if (action.Kind is ComputerActionKind.Wait or ComputerActionKind.Reset) continue;
            if (action.Target is not { } selector || (selector.ObjectId == null && selector.Name == null && selector.AutomationId == null && selector.Text == null))
                throw new ArgumentException("Refreshing targets requires an explicit selector for every input, focus and assertion action.");
            var target = ComputerTargetControl(selector, keyboard: false);
            if (!frame.Targets.TryGetValue(Id(target), out var observed))
                throw new InvalidOperationException("The target was not in the observed element page. Observe its page before acting.");
            var current = ComputerTargetStates(target);
            if (observed.Length != current.Length || observed.Where((state, i) => !state.Matches(current[i])).Any())
                throw new InvalidOperationException("The observed target or its ancestors changed. Observe again before acting.");
            targets[index] = target;
        }
        return targets;
    }

    private static ComputerTargetState[] ComputerTargetStates(Control target) => target.GetVisualAncestors().OfType<Control>().Prepend(target)
        .Select(control => new ComputerTargetState(control, control.DataContext, control.Name, AutomationProperties.GetAutomationId(control), ComputerText(control),
            control.IsEffectivelyEnabled, control.IsEffectivelyVisible, control.IsHitTestVisible, control.Focusable, control.Opacity)).ToArray();

    private Control ComputerTargetControl(ComputerTarget? selector, bool keyboard)
    {
        if (selector?.ObjectId != null) return Resolve(selector.ObjectId) as Control ?? throw new ArgumentException("Select a control.");
        if (selector != null && (selector.Name != null || selector.AutomationId != null || selector.Text != null))
        {
            var matches = _objects.Values.OfType<Control>().Where(control => (selector.Name == null || control.Name == selector.Name) &&
                (selector.AutomationId == null || AutomationProperties.GetAutomationId(control) == selector.AutomationId) &&
                (selector.Text == null || ComputerText(control) == selector.Text)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : throw new InvalidOperationException(matches.Length == 0 ? "No control matches the target." : "Target is ambiguous. Use an observed objectId.");
        }
        if (keyboard)
        {
            if (ComputerTopLevel.FocusManager.GetFocusedElement() is Control focused && WithinRoot(focused)) return focused;
            return _objects.Values.OfType<Control>().FirstOrDefault(control => control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled)
                ?? throw new InvalidOperationException("Click or focus an enabled control before typing.");
        }
        return (Control)_root;
    }
    private static string? ComputerText(Control control) => control switch
    { TextBox text => text.Text, TextBlock text => text.Text, ContentControl { Content: string text } => text, _ => AutomationProperties.GetName(control) };
}
