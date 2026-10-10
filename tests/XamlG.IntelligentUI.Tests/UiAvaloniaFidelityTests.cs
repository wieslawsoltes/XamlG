using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiAvaloniaFidelityTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";

    [AvaloniaFact]
    public void Single_root_receives_finite_viewport_and_star_rows_fill_remaining_space()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("viewport", 0, 1, $"""
            <Grid {Ns} ui:Key="layout" RowDefinitions="40,*">
              <Border ui:Key="header" Grid.Row="0"/>
              <Border ui:Key="body" Grid.Row="1" MinHeight="20"/>
            </Grid>
            """), "owner");
        using var renderer = new UiAvaloniaRenderer();
        renderer.Apply(snapshot);
        Layout(renderer.View, 300, 200);
        Assert.Equal(new Size(300, 200), renderer.Find("/layout")!.Bounds.Size);
        Assert.Equal(new Size(300, 40), renderer.Find("/header")!.Bounds.Size);
        Assert.Equal(new Size(300, 160), renderer.Find("/body")!.Bounds.Size);
        Layout(renderer.View, 500, 320);
        Assert.Equal(new Size(500, 280), renderer.Find("/body")!.Bounds.Size);
    }

    [AvaloniaFact]
    public void Removing_child_detaches_it_and_preserves_unchanged_container_identity()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("lifetime", 0, 1, $"<Border {Ns} ui:Key=\"parent\"><TextBox ui:Key=\"child\" Text=\"retained\"/></Border>"), "owner");
        using var renderer = new UiAvaloniaRenderer();
        renderer.Apply(snapshot);
        var parent = Assert.IsType<Border>(renderer.Find("/parent"));
        var child = Assert.IsType<TextBox>(renderer.Find("/child"));
        snapshot = store.Publish(new("lifetime", snapshot.Revision, 2, $"<Border {Ns} ui:Key=\"parent\"/>"), "owner");
        renderer.Apply(snapshot);
        Assert.Same(parent, renderer.Find("/parent"));
        Assert.Null(child.Parent);
        Assert.Null(parent.Child);
    }

    [AvaloniaFact]
    public void Disposal_detaches_entire_owned_tree_not_only_the_surface_root()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("dispose", 0, 1, $"<Border {Ns} ui:Key=\"parent\"><TextBox ui:Key=\"child\"/></Border>"), "owner");
        var renderer = new UiAvaloniaRenderer();
        renderer.Apply(snapshot);
        var parent = Assert.IsType<Border>(renderer.Find("/parent"));
        var child = Assert.IsType<TextBox>(renderer.Find("/child"));
        renderer.Dispose();
        Assert.Null(parent.Parent);
        Assert.Null(child.Parent);
        Assert.Null(parent.Child);
        Assert.Null(renderer.Find("/child"));
        renderer.Dispose();
    }

    private static void Layout(Control control, double width, double height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
    }
}
