using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using XamlG.AvaloniaRuntime.Design;
using XamlG.Runtime.Design;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DesignerSurfaceTests
{
    [AvaloniaFact]
    public void Group_drag_publishes_one_snapped_source_batch_without_mutating_live_layout()
    {
        using var fixture = new SurfaceFixture();
        fixture.Surface.SelectControls([fixture.A, fixture.B]);
        var revision = fixture.Surface.DesignRevision;
        fixture.Window.MouseDown(new(50, 55), MouseButton.Left);
        Assert.True(fixture.Surface.IsGestureActive);
        fixture.Window.MouseMove(new(63, 48));
        Assert.Empty(fixture.Batches); Assert.Equal(30, Canvas.GetLeft(fixture.A));
        fixture.Window.MouseUp(new(63, 48), MouseButton.Left);
        var edits = Assert.Single(fixture.Batches); Assert.Equal(2, edits.Count);
        Assert.Equal("40", edits[0].Properties["Canvas.Left"]); Assert.Equal("32", edits[0].Properties["Canvas.Top"]);
        Assert.Equal("180", edits[1].Properties["Canvas.Left"]); Assert.Equal("92", edits[1].Properties["Canvas.Top"]);
        Assert.All(edits, edit => Assert.Equal("View.axaml", edit.Source.Path));
        Assert.Equal(30, Canvas.GetLeft(fixture.A)); Assert.Equal(170, Canvas.GetLeft(fixture.B));
        Assert.False(fixture.Surface.IsGestureActive); Assert.True(fixture.Surface.DesignRevision > revision); Assert.Empty(fixture.Errors);
    }

    [AvaloniaTheory]
    [InlineData("escape")]
    [InlineData("mode")]
    [InlineData("grid")]
    [InlineData("content")]
    public void Cancelling_or_reconfiguring_an_active_gesture_never_publishes_source_edits(string action)
    {
        using var fixture = new SurfaceFixture(); fixture.Surface.SelectControls([fixture.A]);
        fixture.Window.MouseDown(new(50, 55), MouseButton.Left); fixture.Window.MouseMove(new(66, 63));
        Assert.True(fixture.Surface.IsGestureActive);
        switch (action)
        {
            case "escape": fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null); fixture.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null); break;
            case "mode": fixture.Surface.IsDesignMode = false; break;
            case "grid": fixture.Surface.GridSize = 4; break;
            case "content": fixture.Surface.Content = new Canvas(); break;
        }
        Assert.False(fixture.Surface.IsGestureActive);
        fixture.Window.MouseUp(new(66, 63), MouseButton.Left);
        Assert.Empty(fixture.Batches); Assert.Empty(fixture.Errors);
    }

    [AvaloniaFact]
    public void Resize_handles_outside_the_control_are_hit_testable_and_enforce_maximum_sizes()
    {
        using var fixture = new SurfaceFixture(); fixture.A.MaxWidth = 70; fixture.Pump();
        fixture.Surface.GridSize = 0; fixture.Surface.SelectControls([fixture.A]);
        fixture.Window.MouseDown(new(73, 73), MouseButton.Left);
        Assert.True(fixture.Surface.IsGestureActive);
        fixture.Window.MouseMove(new(123, 83)); fixture.Window.MouseUp(new(123, 83), MouseButton.Left);
        Assert.Empty(fixture.Batches); Assert.Single(fixture.Errors); Assert.False(fixture.Surface.IsGestureActive);
        Assert.Equal(40, fixture.A.Width);
    }

    [AvaloniaFact]
    public void Runtime_geometry_changes_during_a_gesture_invalidate_its_source_plan()
    {
        using var fixture = new SurfaceFixture(); fixture.Surface.SelectControls([fixture.A]);
        fixture.Window.MouseDown(new(50, 55), MouseButton.Left); fixture.Window.MouseMove(new(66, 63));
        Canvas.SetLeft(fixture.A, 90); fixture.Pump();
        fixture.Window.MouseUp(new(66, 63), MouseButton.Left);
        Assert.Empty(fixture.Batches); Assert.Single(fixture.Errors); Assert.Equal(90, Canvas.GetLeft(fixture.A));
    }

    [AvaloniaFact]
    public void Keyboard_group_nudges_and_source_selection_keep_paths_versions_and_atomic_batches()
    {
        using var fixture = new SurfaceFixture(); fixture.Surface.SelectControls([fixture.A, fixture.B]);
        Assert.True(fixture.Surface.Focus());
        fixture.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null); fixture.Window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
        var edits = Assert.Single(fixture.Batches); Assert.Equal(2, edits.Count);
        Assert.Equal("31", edits[0].Properties["Canvas.Left"]); Assert.Equal("171", edits[1].Properties["Canvas.Left"]);
        var source = edits[0].Source;
        Assert.False(fixture.Surface.SelectSource("Other.axaml", source.Start, source.Version));
        Assert.False(fixture.Surface.SelectSource(source.Path, source.Start, source.Version + 1));
        Assert.True(fixture.Surface.SelectSource(source.Path, source.Start, source.Version));
        Assert.Same(fixture.A, Assert.Single(fixture.Surface.SelectedControls));
        Assert.Throws<ArgumentException>(() => fixture.Surface.SelectControls([fixture.Root, fixture.A]));
        Assert.Throws<ArgumentException>(() => fixture.Surface.SelectControls([fixture.A, fixture.A]));
        Assert.Throws<ArgumentException>(() => fixture.Surface.SelectControls([new Border()]));
    }

    private sealed class SurfaceFixture : IDisposable
    {
        public SurfaceFixture()
        {
            var xaml = $"<Canvas {ResourceProjectFixture.Namespace}><Border Name='a' Canvas.Left='30' Canvas.Top='40' Width='40' Height='30' Background='Red'/><Border Name='b' Canvas.Left='170' Canvas.Top='100' Width='60' Height='50' Background='Blue'/></Canvas>";
            Root = Assert.IsType<Canvas>(AvaloniaCompilation.Build(xaml, true, "View.axaml"));
            A = Assert.IsType<Border>(Root.Children[0]); B = Assert.IsType<Border>(Root.Children[1]);
            Surface = new() { Content = Root, IsDesignMode = true };
            Surface.EditsRequested += edits => Batches.Add(edits); Surface.EditFailed += Errors.Add;
            Window = new() { Content = Surface, Width = 400, Height = 300 }; Window.Show(); Pump();
        }
        public Canvas Root { get; }
        public Border A { get; }
        public Border B { get; }
        public AvaloniaDesignerSurface Surface { get; }
        public Window Window { get; }
        public List<IReadOnlyList<XamlVisualEdit>> Batches { get; } = [];
        public List<Exception> Errors { get; } = [];
        public void Pump() { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        public void Dispose() => Window.Close();
    }
}
