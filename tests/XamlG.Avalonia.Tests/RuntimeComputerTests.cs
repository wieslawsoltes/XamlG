using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 200 };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window;
    }
}
