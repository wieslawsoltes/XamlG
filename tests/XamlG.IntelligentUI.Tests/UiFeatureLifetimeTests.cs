using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiFeatureLifetimeTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";

    [AvaloniaFact]
    public void New_typed_setters_accept_the_existing_catalog_tuple_grammar()
    {
        var snapshot = new UiSessionStore().Publish(new("tuples", 0, 1, $"""
            <Label {Ns} ui:Key="label" Padding="2,,4" CornerRadius="3, ,5"
                   BorderThickness="1, ,2,3,4" Content="Tuple compatibility"/>
            """), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var label = Assert.IsType<Label>(renderer.Find("/label"));
        Assert.Equal(new Thickness(2, 4), label.Padding);
        Assert.Equal(new CornerRadius(3, 5, 3, 5), label.CornerRadius);
        Assert.Equal(new Thickness(1, 2, 3, 4), label.BorderThickness);
    }

    [AvaloniaFact]
    public void Retiring_layout_transform_disables_its_upstream_property_subscription()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("bridge", 0, 1, $"<LayoutTransformControl {Ns} ui:Key=\"transform\" UseRenderTransform=\"True\"><Border/></LayoutTransformControl>"), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var transform = Assert.IsType<LayoutTransformControl>(renderer.Find("/transform"));
        Assert.True(transform.UseRenderTransform);
        snapshot = store.Publish(new("bridge", snapshot.Revision, 2, $"<Label {Ns} Content=\"Replaced\"/>"), "owner");
        renderer.Apply(snapshot);
        Assert.False(transform.UseRenderTransform);
        Assert.Null(transform.Child);
        var unrelated = new LayoutTransformControl { RenderTransform = new ScaleTransform(2, 2) };
        Assert.Null(unrelated.LayoutTransform);
    }

    [AvaloniaFact]
    public void Disposing_renderer_disables_the_layout_transform_bridge()
    {
        var snapshot = new UiSessionStore().Publish(new("dispose-bridge", 0, 1, $"<LayoutTransformControl {Ns} ui:Key=\"transform\" UseRenderTransform=\"True\"/>"), "owner");
        var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var transform = Assert.IsType<LayoutTransformControl>(renderer.Find("/transform"));
        renderer.Dispose();
        Assert.False(transform.UseRenderTransform);
        Assert.Null(transform.Parent);
    }
}
