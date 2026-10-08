using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using XamlG.AvaloniaRuntime.Inspection;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RuntimeComputerTests
{
    [AvaloniaFact]
    public async Task A_sequence_clicks_types_tabs_and_verifies_the_actual_application()
    {
        var first = new TextBox { Name = "first", Width = 200 };
        var second = new TextBox { Name = "second", Width = 200 };
        var button = new Button { Name = "add", Content = "Add" }; var clicks = 0;
        button.Click += (_, _) => clicks++;
        var root = new StackPanel { Children = { first, second, button } }; var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root);
            var frame = inspector.ObserveComputer(new(Screenshot: false)).Observation;
            var result = await inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision,
            [new(ComputerActionKind.Click, new(Name: "first")), new(ComputerActionKind.Text, Text: "one"),
                new(ComputerActionKind.Key, Key: "Tab"), new(ComputerActionKind.Text, Text: "two"),
                new(ComputerActionKind.Click, new(Name: "add")), new(ComputerActionKind.Assert, new(Name: "second"), Text: "two")], Screenshot: false), TestContext.Current.CancellationToken);
            Assert.Null(result.Error); Assert.Equal(6, result.Completed.Count);
            Assert.Equal("one", first.Text); Assert.Equal("two", second.Text); Assert.Equal(1, clicks);
            Assert.NotEqual(frame.FrameId, result.Capture.Observation.FrameId);
            Assert.Contains(result.Capture.Observation.Elements, element => element.Name == "first" && element.Text == "one");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Stale_frames_are_rejected_and_partial_sequences_report_their_completed_effects()
    {
        var button = new Button { Name = "action", Content = "Before" }; var clicks = 0;
        button.Click += (_, _) => clicks++;
        var window = Show(button);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(button);
            var stale = inspector.ObserveComputer(new(Screenshot: false)).Observation;
            button.Content = "After";
            await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ComputerActionsAsync(new(stale.FrameId, stale.Revision,
                [new(ComputerActionKind.Click)], Screenshot: false), TestContext.Current.CancellationToken));
            Assert.Equal(0, clicks);
            var current = inspector.ObserveComputer(new(Screenshot: false)).Observation;
            var result = await inspector.ComputerActionsAsync(new(current.FrameId, current.Revision,
                [new(ComputerActionKind.Click), new(ComputerActionKind.Assert, Text: "wrong"), new(ComputerActionKind.Click)], Screenshot: false), TestContext.Current.CancellationToken);
            Assert.Equal(1, result.FailedIndex); Assert.Single(result.Completed); Assert.NotNull(result.Error); Assert.Equal(1, clicks);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Screenshot_coordinates_map_to_the_observed_viewport_and_cannot_escape_it()
    {
        var button = new Button { Name = "target", Width = 100, Height = 40 }; var clicks = 0;
        button.Click += (_, _) => clicks++;
        var window = Show(button);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(button);
            var frame = inspector.ObserveComputer(new(Screenshot: false, MaximumWidth: 200, MaximumHeight: 100)).Observation;
            Assert.Equal(0.5, frame.ImageScale);
            var target = frame.Elements.Single(element => element.Name == "target");
            var result = await inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision,
                [new(ComputerActionKind.Click, X: (target.X + target.Width / 2) * frame.ImageScale, Y: (target.Y + target.Height / 2) * frame.ImageScale)], Screenshot: false), TestContext.Current.CancellationToken);
            Assert.Null(result.Error); Assert.Equal(1, clicks);
            var fresh = result.Capture.Observation;
            var rejected = await inspector.ComputerActionsAsync(new(fresh.FrameId, fresh.Revision,
                [new(ComputerActionKind.Click, X: -1, Y: 0)], Screenshot: false), TestContext.Current.CancellationToken);
            Assert.Empty(rejected.Completed); Assert.Contains("outside", rejected.Error); Assert.Equal(1, clicks);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Explicit_targets_survive_unrelated_live_updates_and_use_their_current_position()
    {
        var clock = new TextBlock { Text = "12:00" };
        var input = new TextBox { Name = "input", Width = 200 };
        var button = new Button { Name = "save", Content = "Save" }; var clicks = 0;
        button.Click += (_, _) => clicks++;
        var root = new StackPanel { Children = { clock, input, button } }; var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root);
            var frame = inspector.ObserveComputer(new(Screenshot: false)).Observation;
            clock.Text = "12:01"; clock.Height = 60; Layout(window);
            await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision,
                [new(ComputerActionKind.Click, new(Name: "save"))], Screenshot: false), TestContext.Current.CancellationToken));
            var result = await inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision,
                [new(ComputerActionKind.Text, new(Name: "input"), Text: "Saved"), new(ComputerActionKind.Click, new(Name: "save")),
                 new(ComputerActionKind.Assert, new(Name: "input"), Text: "Saved")], Screenshot: false, RefreshTargets: true), TestContext.Current.CancellationToken);
            Assert.Null(result.Error); Assert.Equal(3, result.Completed.Count); Assert.Equal(1, clicks); Assert.Equal("Saved", input.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("text")]
    [InlineData("dataContext")]
    [InlineData("ancestorDataContext")]
    [InlineData("automationId")]
    [InlineData("enabled")]
    [InlineData("visible")]
    [InlineData("hitTest")]
    [InlineData("opacity")]
    [InlineData("replacement")]
    [InlineData("reparent")]
    public async Task Changed_later_targets_reject_the_entire_refreshed_batch_before_input(string change)
    {
        var first = new Button { Name = "first", Content = "First" }; var clicks = 0;
        var later = new Button { Name = "later", Content = "Later", DataContext = new EqualDataContext() };
        first.Click += (_, _) => clicks++;
        var parent = new StackPanel { Children = { later } };
        var root = new StackPanel { Children = { first, parent } }; var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root);
            var frame = inspector.ObserveComputer(new(Screenshot: false)).Observation;
            switch (change)
            {
                case "text": later.Content = "Changed"; break;
                case "dataContext": later.DataContext = new EqualDataContext(); break;
                case "ancestorDataContext": parent.DataContext = new object(); break;
                case "automationId": AutomationProperties.SetAutomationId(later, "changed"); break;
                case "enabled": later.IsEnabled = false; break;
                case "visible": later.IsVisible = false; break;
                case "hitTest": later.IsHitTestVisible = false; break;
                case "opacity": later.Opacity = 0; break;
                case "replacement": parent.Children.Clear(); parent.Children.Add(new Button { Name = "later", Content = "Later" }); break;
                case "reparent": parent.Children.Remove(later); root.Children.Add(later); break;
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision,
                [new(ComputerActionKind.Click, new(Name: "first")), new(ComputerActionKind.Click, new(Name: "later"))],
                Screenshot: false, RefreshTargets: true), TestContext.Current.CancellationToken));
            Assert.Equal(0, clicks);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("coordinates")]
    [InlineData("path")]
    [InlineData("implicitFocus")]
    [InlineData("unobserved")]
    [InlineData("wrongRevision")]
    [InlineData("viewport")]
    public async Task Refreshed_input_cannot_reuse_unobserved_or_coordinate_targets(string change)
    {
        var button = new Button { Name = "button", Content = "Go" }; var clicks = 0;
        button.Click += (_, _) => clicks++;
        var root = new StackPanel { Children = { button } }; var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root);
            var frame = inspector.ObserveComputer(new(Screenshot: false, Count: change == "unobserved" ? 1 : 100)).Observation;
            var action = new ComputerAction(ComputerActionKind.Click, new(Name: "button"));
            if (change == "coordinates") action = action with { X = 50, Y = 50 };
            if (change == "path") action = action with { Path = [new(50, 50)] };
            if (change == "implicitFocus") action = new(ComputerActionKind.Text, Text: "secret");
            if (change == "viewport") { window.Width = 500; Layout(window); Assert.NotEqual(frame.Width, window.Bounds.Width); }
            var request = new ComputerActionsRequest(frame.FrameId, frame.Revision + (change == "wrongRevision" ? 1 : 0), [action], Screenshot: false, RefreshTargets: true);
            var error = await Record.ExceptionAsync(() => inspector.ComputerActionsAsync(request, TestContext.Current.CancellationToken));
            Assert.True(error is ArgumentException or InvalidOperationException, error?.ToString()); Assert.Equal(0, clicks);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refreshed_pointer_and_touch_input_cannot_hit_a_covering_sibling(bool touch)
    {
        var button = new Button { Name = "target", Content = "Target" }; var clicks = 0; var overlayInput = 0;
        var overlay = new Border { Background = Brushes.Red, IsHitTestVisible = false, Opacity = 0 };
        button.Click += (_, _) => clicks++; overlay.PointerPressed += (_, _) => overlayInput++;
        var root = new Grid { Children = { button, overlay } }; var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root);
            var frame = inspector.ObserveComputer(new(Screenshot: false)).Observation;
            overlay.IsHitTestVisible = true; overlay.Opacity = 1;
            var result = await inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision,
                [new(touch ? ComputerActionKind.Touch : ComputerActionKind.Click, new(Name: "target"), ContactId: 1)],
                Screenshot: false, RefreshTargets: true), TestContext.Current.CancellationToken);
            Assert.Contains("covered", result.Error); Assert.Equal(0, result.FailedIndex); Assert.Empty(result.Completed);
            Assert.Equal(0, clicks); Assert.Equal(0, overlayInput);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Refreshed_targets_stay_pinned_when_an_earlier_action_replaces_a_control()
    {
        var first = new Button { Name = "first", Content = "Replace" };
        var later = new Button { Name = "later", Content = "Later" }; var clicks = 0;
        var replacement = new Button { Name = "later", Content = "Later" };
        replacement.Click += (_, _) => clicks++;
        var root = new StackPanel { Children = { first, later } }; var window = Show(root);
        first.Click += (_, _) => { root.Children.Remove(later); root.Children.Add(replacement); };
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root);
            var frame = inspector.ObserveComputer(new(Screenshot: false)).Observation;
            var result = await inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision,
                [new(ComputerActionKind.Click, new(Name: "first")), new(ComputerActionKind.Click, new(Name: "later"))],
                Screenshot: false, RefreshTargets: true), TestContext.Current.CancellationToken);
            Assert.NotNull(result.Error); Assert.Equal(1, result.FailedIndex); Assert.Single(result.Completed); Assert.Equal(0, clicks);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refreshed_gestures_require_releasing_previous_capture_and_can_reset_it_explicitly(bool touch)
    {
        var clock = new TextBlock { Text = "12:00" };
        var button = new Button { Name = "target", Content = "Go" }; var clicks = 0;
        button.Click += (_, _) => clicks++;
        var root = new StackPanel { Children = { clock, button } }; var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root);
            var frame = inspector.ObserveComputer(new(Screenshot: false)).Observation;
            var begin = new ComputerAction(touch ? ComputerActionKind.Touch : ComputerActionKind.Down, new(Name: "target"), ContactId: 1);
            var end = new ComputerAction(touch ? ComputerActionKind.Touch : ComputerActionKind.Up, new(Name: "target"), ContactId: 1, TouchAction: RuntimeTouchAction.End);
            var held = await inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision, [begin], Screenshot: false), TestContext.Current.CancellationToken);
            Assert.Null(held.Error); frame = held.Capture.Observation; clock.Text = "12:01";
            var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision,
                [begin, end], Screenshot: false, RefreshTargets: true), TestContext.Current.CancellationToken));
            Assert.Contains("held input", rejection.Message); Assert.Equal(0, clicks);
            var result = await inspector.ComputerActionsAsync(new(frame.FrameId, frame.Revision,
                [new(ComputerActionKind.Reset), begin, end], Screenshot: false, RefreshTargets: true), TestContext.Current.CancellationToken);
            Assert.Null(result.Error); Assert.Equal(3, result.Completed.Count); Assert.Equal(1, clicks); Assert.False(button.IsPressed);
        }
        finally { window.Close(); }
    }

    private sealed class EqualDataContext
    {
        public override bool Equals(object? obj) => obj is EqualDataContext;
        public override int GetHashCode() => 0;
    }

    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 200 };
        window.Show(); Layout(window);
        return window;
    }
    private static void Layout(Window window)
    { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
}
