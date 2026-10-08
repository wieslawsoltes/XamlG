using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using XamlG.AvaloniaRuntime.Inspection;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RuntimeInputTests
{
    [AvaloniaFact]
    public void Text_and_key_input_use_textbox_editing_and_deliver_typed_modifiers()
    {
        var box = new TextBox(); var window = Show(box);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(box); var id = inspector.Capture().RootId;
            var keys = new List<(Key Key, KeyModifiers Modifiers)>();
            box.AddHandler(InputElement.KeyDownEvent, (_, args) => keys.Add((args.Key, args.KeyModifiers)), RoutingStrategies.Bubble, true);
            inspector.SendText(id, "hello", inspector.Revision);
            Assert.Equal("hello", box.Text); Assert.True(box.IsFocused);
            inspector.SendKey(id, "Back", RuntimeKeyAction.Press, inspector.Revision);
            Assert.Equal("hell", box.Text);
            inspector.SendKey(id, "Home", RuntimeKeyAction.Press, inspector.Revision);
            inspector.SendText(id, "X", inspector.Revision);
            Assert.Equal("Xhell", box.Text);
            inspector.SendKey(id, "Right", RuntimeKeyAction.Press, inspector.Revision, ["Shift"]);
            Assert.Contains((Key.Right, KeyModifiers.Shift), keys);
            Assert.Equal(1, Math.Abs(box.SelectionEnd - box.SelectionStart));
            inspector.SendText(id, "!", inspector.Revision);
            Assert.Equal("X!ell", box.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Pointer_click_activates_a_real_button_and_held_buttons_require_matching_release()
    {
        var button = new Button { Content = "Run" }; var window = Show(button); var clicks = 0;
        button.Click += (_, _) => clicks++;
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(button); var id = inspector.Capture().RootId;
            inspector.SendPointer(id, RuntimePointerAction.Click, inspector.Revision);
            Assert.Equal(1, clicks); Assert.False(button.IsPressed);
            inspector.SendPointer(id, RuntimePointerAction.Down, inspector.Revision);
            Assert.True(button.IsPressed);
            Assert.Throws<InvalidOperationException>(() => inspector.SendPointer(id, RuntimePointerAction.Down, inspector.Revision));
            Assert.Throws<InvalidOperationException>(() => inspector.SendPointer(id, RuntimePointerAction.Click, inspector.Revision));
            inspector.SendPointer(id, RuntimePointerAction.Up, inspector.Revision);
            Assert.Equal(2, clicks); Assert.False(button.IsPressed);
            Assert.Throws<InvalidOperationException>(() => inspector.SendPointer(id, RuntimePointerAction.Up, inspector.Revision));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Mouse_capture_survives_drag_steps_and_reset_releases_it()
    {
        var first = Target("first"); var second = Target("second"); Canvas.SetLeft(second, 200);
        var canvas = new Canvas { Children = { first, second } }; var window = Show(canvas);
        IPointer? pointer = null; Point moved = default; var lost = 0;
        first.PointerPressed += (_, args) => { pointer = args.Pointer; pointer.Capture(first); };
        first.PointerMoved += (_, args) => moved = args.GetPosition(first);
        first.PointerCaptureLost += (_, _) => lost++;
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(canvas); var state = inspector.Capture();
            var firstId = state.Nodes.Single(node => node.Name == "first").Id; var secondId = state.Nodes.Single(node => node.Name == "second").Id;
            inspector.SendPointer(firstId, RuntimePointerAction.Down, inspector.Revision, 5, 5);
            Assert.Same(first, pointer!.Captured);
            inspector.SendPointer(secondId, RuntimePointerAction.Move, inspector.Revision, 10, 10);
            Assert.Equal(new Point(210, 10), moved); Assert.Same(first, pointer.Captured);
            inspector.ResetInput(inspector.Revision);
            Assert.Null(pointer.Captured); Assert.Equal(1, lost);
            Assert.Throws<InvalidOperationException>(() => inspector.SendPointer(firstId, RuntimePointerAction.Up, inspector.Revision));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Double_click_and_wheel_deliver_framework_pointer_arguments()
    {
        var target = Target("target"); var window = Show(target); var counts = new List<int>(); Vector wheel = default; KeyModifiers modifiers = default;
        target.PointerPressed += (_, args) => counts.Add(args.ClickCount);
        target.PointerWheelChanged += (_, args) => { wheel = args.Delta; modifiers = args.KeyModifiers; };
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(target); var id = inspector.Capture().RootId;
            inspector.SendPointer(id, RuntimePointerAction.DoubleClick, inspector.Revision);
            Assert.Equal([1, 2], counts);
            inspector.SendPointer(id, RuntimePointerAction.Wheel, inspector.Revision, modifiers: ["Shift"], deltaX: 1, deltaY: -3);
            Assert.Equal(new Vector(1, -3), wheel); Assert.Equal(KeyModifiers.Shift, modifiers);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Input_rejects_stale_disabled_hidden_detached_and_out_of_tree_targets()
    {
        var target = Target("target"); var outside = Target("outside"); Canvas.SetLeft(outside, 200);
        var canvas = new Canvas { Children = { target, outside } }; var window = Show(canvas); var outsidePresses = 0;
        outside.PointerPressed += (_, _) => outsidePresses++;
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(target); var state = inspector.Capture();
            target.IsEnabled = false;
            Assert.Throws<InvalidOperationException>(() => inspector.SendPointer(state.RootId, RuntimePointerAction.Click, state.Revision));
            Assert.Throws<InvalidOperationException>(() => inspector.SendPointer(state.RootId, RuntimePointerAction.Click, inspector.Revision));
            target.IsEnabled = true; target.IsVisible = false;
            Assert.Throws<InvalidOperationException>(() => inspector.SendPointer(state.RootId, RuntimePointerAction.Click, inspector.Revision));
            target.IsVisible = true; window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.Throws<InvalidOperationException>(() => inspector.SendPointer(state.RootId, RuntimePointerAction.Click, inspector.Revision, 210, 10));
            Assert.Equal(0, outsidePresses);
            canvas.Children.Remove(target); inspector.Capture();
            Assert.Throws<InvalidOperationException>(() => inspector.SendPointer(state.RootId, RuntimePointerAction.Click, inspector.Revision));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Touch_contacts_have_independent_capture_enforce_the_limit_and_reset_cleanly()
    {
        var target = Target("target"); var window = Show(target);
        var pointers = new List<IPointer>(); var moves = new List<Point>(); var released = 0;
        target.PointerPressed += (_, args) => { Assert.Equal(PointerType.Touch, args.Pointer.Type); pointers.Add(args.Pointer); };
        target.PointerMoved += (_, args) => moves.Add(args.GetPosition(target));
        target.PointerReleased += (_, _) => released++;
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(target); var id = inspector.Capture().RootId;
            inspector.SendTouch(id, 1, RuntimeTouchAction.Begin, 10, 10, inspector.Revision);
            Assert.Same(target, Assert.Single(pointers).Captured);
            inspector.SendTouch(id, 1, RuntimeTouchAction.Move, 20, 25, inspector.Revision);
            Assert.Contains(new Point(20, 25), moves);
            inspector.SendTouch(id, 1, RuntimeTouchAction.End, 20, 25, inspector.Revision);
            Assert.Equal(1, released); Assert.Null(pointers[0].Captured);
            Assert.Throws<InvalidOperationException>(() => inspector.SendTouch(id, 1, RuntimeTouchAction.End, 20, 25, inspector.Revision));
            for (var i = 1; i <= 16; i++) inspector.SendTouch(id, i, RuntimeTouchAction.Begin, i, 10, inspector.Revision);
            Assert.Equal(16, pointers.Skip(1).Select(pointer => pointer.Id).Distinct().Count());
            Assert.Throws<InvalidOperationException>(() => inspector.SendTouch(id, 17, RuntimeTouchAction.Begin, 17, 10, inspector.Revision));
            inspector.ResetInput(inspector.Revision); Assert.All(pointers, pointer => Assert.Null(pointer.Captured));
            inspector.SendTouch(id, 1, RuntimeTouchAction.Begin, 10, 10, inspector.Revision);
            inspector.SendTouch(id, 1, RuntimeTouchAction.Cancel, 10, 10, inspector.Revision);
            Assert.Null(pointers[^1].Captured); Assert.Equal(1, released);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Touch_capture_inside_the_preview_can_drag_over_an_outside_sibling()
    {
        var target = Target("target"); var outside = Target("outside"); Canvas.SetLeft(outside, 200);
        var canvas = new Canvas { Children = { target, outside } }; var window = Show(canvas); Point moved = default; var outsideMoves = 0;
        target.PointerMoved += (_, args) => moved = args.GetPosition(target);
        outside.PointerMoved += (_, _) => outsideMoves++;
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(target); var id = inspector.Capture().RootId;
            inspector.SendTouch(id, 1, RuntimeTouchAction.Begin, 10, 10, inspector.Revision);
            inspector.SendTouch(id, 1, RuntimeTouchAction.Move, 210, 10, inspector.Revision);
            Assert.Equal(new Point(210, 10), moved); Assert.Equal(0, outsideMoves);
            inspector.SendTouch(id, 1, RuntimeTouchAction.End, 210, 10, inspector.Revision);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Touch_capture_outside_the_preview_is_rejected_before_another_event_is_dispatched()
    {
        var target = Target("target"); var outside = Target("outside"); Canvas.SetLeft(outside, 200);
        var canvas = new Canvas { Children = { target, outside } }; var window = Show(canvas); IPointer? pointer = null; var outsideMoves = 0;
        target.PointerPressed += (_, args) => { pointer = args.Pointer; pointer.Capture(outside); };
        outside.PointerMoved += (_, _) => outsideMoves++;
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(target); var id = inspector.Capture().RootId;
            inspector.SendTouch(id, 1, RuntimeTouchAction.Begin, 10, 10, inspector.Revision);
            Assert.Same(outside, pointer!.Captured);
            Assert.Throws<InvalidOperationException>(() => inspector.SendTouch(id, 1, RuntimeTouchAction.Move, 20, 10, inspector.Revision));
            Assert.Equal(0, outsideMoves); Assert.Null(pointer.Captured);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void A_new_touch_cannot_borrow_mouse_capture_to_target_an_outside_control()
    {
        var target = Target("target"); var outside = Target("outside"); Canvas.SetLeft(outside, 200);
        var canvas = new Canvas { Children = { target, outside } }; var window = Show(canvas); var outsidePresses = 0;
        target.PointerPressed += (_, args) => args.Pointer.Capture(target);
        outside.PointerPressed += (_, _) => outsidePresses++;
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(target); var id = inspector.Capture().RootId;
            inspector.SendPointer(id, RuntimePointerAction.Down, inspector.Revision, 10, 10);
            Assert.Throws<InvalidOperationException>(() => inspector.SendTouch(id, 1, RuntimeTouchAction.Begin, 210, 10, inspector.Revision));
            Assert.Equal(0, outsidePresses);
            inspector.SendPointer(id, RuntimePointerAction.Up, inspector.Revision, 10, 10);
            inspector.SendTouch(id, 1, RuntimeTouchAction.Begin, 10, 10, inspector.Revision);
            inspector.SendTouch(id, 1, RuntimeTouchAction.End, 10, 10, inspector.Revision);
        }
        finally { window.Close(); }
    }

    private static Border Target(string name) => new() { Name = name, Width = 80, Height = 80, Background = Brushes.Red };
    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 200 };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); return window;
    }
}
