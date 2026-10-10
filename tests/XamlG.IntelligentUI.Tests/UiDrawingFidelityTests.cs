using System.Collections.Immutable;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using XamlG.IntelligentUI.Avalonia;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiDrawingFidelityTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    [AvaloniaTheory]
    [InlineData("Path", "Data=\"M0,0 L40,0 Q60,20 40,40 C30,50 10,50 0,40 Z\"")]
    [InlineData("Polygon", "Points=\"0,0 40,0 20,40\" FillRule=\"NonZero\"")]
    [InlineData("Polyline", "Points=\"0,0 40,0 20,40\"")]
    [InlineData("Rectangle", "RadiusX=\"4\" RadiusY=\"6\"")]
    [InlineData("Ellipse", "")]
    [InlineData("Line", "StartPoint=\"0,0\" EndPoint=\"40,40\"")]
    public void Native_drawings_and_static_exports_preserve_visual_properties(string type, string geometry)
    {
        var snapshot = new UiSessionStore().Publish(new("drawing", 0, 1, $"""
            <{type} {Ns} ui:Key="drawing" {geometry} Fill="Blue" Stroke="Red" StrokeThickness="2"
                StrokeDashArray="2,3" StrokeDashOffset="1" StrokeLineCap="Round" StrokeJoin="Bevel"
                StrokeMiterLimit="8" Stretch="Uniform" Width="120" Height="80"
                RenderTransform="matrix(1,0.25,0,1,12,-4)" RenderTransformOrigin="25%,75%"
                Clip="M0,0 L120,0 120,80 0,80 Z" ZIndex="7" Margin="-2,3,4,5"/>
            """), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var native = Assert.IsAssignableFrom<Shape>(renderer.Find("/drawing"));
        var exported = Assert.IsAssignableFrom<Shape>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(snapshot)));
        foreach (var shape in new[] { native, exported })
        {
            Assert.Equal(type, shape.GetType().Name);
            Assert.Equal(new Matrix(1, .25, 0, 1, 12, -4), shape.RenderTransform!.Value);
            Assert.Equal(new RelativePoint(.25, .75, RelativeUnit.Relative), shape.RenderTransformOrigin);
            Assert.NotNull(shape.Clip); Assert.NotNull(shape.DefiningGeometry);
            Assert.Equal(new double[] { 2, 3 }, shape.StrokeDashArray!.ToArray());
            Assert.Equal(1, shape.StrokeDashOffset);
            Assert.Equal(PenLineCap.Round, shape.StrokeLineCap);
            Assert.Equal(PenLineJoin.Bevel, shape.StrokeJoin);
            Assert.Equal(Stretch.Uniform, shape.Stretch);
            Assert.Equal(7, shape.ZIndex);
            Assert.Equal(new Thickness(-2, 3, 4, 5), shape.Margin);
        }
    }

    [AvaloniaFact]
    public void Removing_visual_properties_clears_local_values_without_recreating_control()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("retained", 0, 1, $"<Path {Ns} ui:Key=\"p\" Data=\"M0,0 L10,10\" RenderTransform=\"matrix(2,0,0,2,0,0)\" Clip=\"M0,0 L10,0 10,10 Z\" StrokeDashArray=\"2,3\" ZIndex=\"4\"/>"), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var path = Assert.IsType<Path>(renderer.Find("/p"));
        snapshot = store.Publish(new("retained", snapshot.Revision, 2, $"<Path {Ns} ui:Key=\"p\" Data=\"M0,0 L20,20\"/>"), "owner");
        renderer.Apply(snapshot);
        Assert.Same(path, renderer.Find("/p"));
        Assert.Null(path.RenderTransform); Assert.Null(path.Clip); Assert.Null(path.StrokeDashArray); Assert.Equal(0, path.ZIndex);
        Assert.Equal(new Rect(0, 0, 20, 20), path.Data!.Bounds);
    }

    [AvaloniaFact]
    public void Labels_typography_content_alignment_and_scroll_policies_use_real_native_properties()
    {
        var snapshot = new UiSessionStore().Publish(new("text", 0, 1, $"""
            <StackPanel {Ns} Background="White" FlowDirection="RightToLeft" UseLayoutRounding="False">
              <Label ui:Key="label" Content="Hello" Padding="2,4" FontFamily="Arial" FontStyle="Italic"
                  FontSize="128" Foreground="Blue" CornerRadius="4" HorizontalContentAlignment="Right" VerticalContentAlignment="Bottom"/>
              <TextBlock ui:Key="text" Text="Wrapping and trimming" FontSize="6" TextTrimming="WordEllipsis" MaxLines="2" LineHeight="12"/>
              <TextBox ui:Key="editor" TextWrapping="Wrap" AcceptsTab="True"/>
              <ScrollViewer ui:Key="scroll" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Visible" AllowAutoHide="False"/>
            </StackPanel>
            """), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var label = Assert.IsType<Label>(renderer.Find("/label"));
        Assert.Equal("Arial", label.FontFamily.Name); Assert.Equal(128, label.FontSize);
        Assert.Equal(FontStyle.Italic, label.FontStyle); Assert.Equal(new Thickness(2, 4), label.Padding);
        Assert.Equal(HorizontalAlignment.Right, label.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Bottom, label.VerticalContentAlignment);
        Assert.Equal(FlowDirection.RightToLeft, label.FlowDirection);
        var text = Assert.IsType<TextBlock>(renderer.Find("/text"));
        Assert.Same(TextTrimming.WordEllipsis, text.TextTrimming); Assert.Equal(2, text.MaxLines); Assert.Equal(6, text.FontSize);
        var editor = Assert.IsType<TextBox>(renderer.Find("/editor"));
        Assert.True(editor.AcceptsTab); Assert.Equal(TextWrapping.Wrap, editor.TextWrapping);
        var scroll = Assert.IsType<ScrollViewer>(renderer.Find("/scroll"));
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Visible, scroll.VerticalScrollBarVisibility); Assert.False(scroll.AllowAutoHide);
    }

    [AvaloniaFact]
    public void Layout_transform_participates_in_measure_not_only_rendering()
    {
        var snapshot = new UiSessionStore().Publish(new("layout-transform", 0, 1, $"""
            <LayoutTransformControl {Ns} ui:Key="transform" LayoutTransform="matrix(0,1,-1,0,0,0)"
                HorizontalAlignment="Left" VerticalAlignment="Top">
              <Border Width="60" Height="20"/>
            </LayoutTransformControl>
            """), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        renderer.View.Measure(new Size(200, 200));
        Assert.Equal(new Size(20, 60), Assert.IsType<LayoutTransformControl>(renderer.Find("/transform")).DesiredSize);
        Assert.IsType<LayoutTransformControl>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(snapshot)));
    }

    [Theory]
    [InlineData("M0,0 L10,10z")]
    [InlineData("m.5-.5 1e1,2e-1 h2 v3 c1,2 3,4 5,6 s7,8 9,10 q1,2 3,4 t5,6 z")]
    [InlineData("F1 M0,0 A10,20 45 0 1 30,40 Z M2,2 L3,3")]
    public void Valid_bounded_path_grammar_accepts_relative_curves_arcs_and_exponents(string path)
        => UiDrawingValues.ValidatePath(path);

    [Theory]
    [InlineData("L0,0")][InlineData("M0")][InlineData("M0,0 L")][InlineData("M0,0 X1,2")]
    [InlineData("M0,0 A-1,2 0 0 1 3,4")][InlineData("M0,0 A1,2 0 2 1 3,4")]
    [InlineData("M0,0 LNaN,0")][InlineData("M0,0 L1e99,0")][InlineData("M0,0 L1,,2")]
    [InlineData("F2 M0,0")][InlineData("M0,0Z1,2")][InlineData("M0,0,")]
    public void Invalid_paths_are_rejected(string path) => Assert.Throws<UiException>(() => UiDrawingValues.ValidatePath(path));

    [Theory]
    [InlineData("<Path Data=\"M0,0 L1\"/>")]
    [InlineData("<Polygon Points=\"0,0,1\"/>")]
    [InlineData("<Polyline Points=\"NaN,0\"/>")]
    [InlineData("<Border RenderTransform=\"rotate(90)\"/>")]
    [InlineData("<Border RenderTransform=\"matrix(1,0,0,1,0)\"/>")]
    [InlineData("<Border RenderTransformOrigin=\"50%,10\"/>")]
    [InlineData("<Border Clip=\"https://example.com/drawing.svg\"/>")]
    [InlineData("<Path StrokeDashArray=\"0,0\"/>")]
    [InlineData("<Path StrokeDashArray=\"-1,2\"/>")]
    [InlineData("<Label FontFamily=\"avares://untrusted/font.ttf\"/>")]
    [InlineData("<Border MinWidth=\"20\" MaxWidth=\"10\"/>")]
    public void Invalid_drawing_or_layout_cannot_replace_a_committed_snapshot(string child)
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("atomic", 0, 1, $"<Border {Ns}/>"), "owner");
        Assert.Throws<UiException>(() => store.Publish(new("atomic", snapshot.Revision, 2, $"<StackPanel {Ns}>{child}</StackPanel>"), "owner"));
        Assert.Same(snapshot, store.Read("atomic", "owner"));
    }

    [Fact]
    public void Drawing_budgets_bound_points_segments_dashes_and_text()
    {
        Assert.Throws<UiException>(() => UiDrawingValues.ReadPoints(string.Join(' ', Enumerable.Repeat("1,2", 513))));
        Assert.Throws<UiException>(() => UiDrawingValues.ReadDashes(string.Join(',', Enumerable.Repeat("1", 65))));
        Assert.Throws<UiException>(() => UiDrawingValues.ValidatePath("M0,0 " + string.Join(' ', Enumerable.Repeat("L1,2", 512))));
        Assert.Throws<UiException>(() => UiDrawingValues.ValidatePath(new string(' ', 16385)));
        Assert.Equal(new double[] { 1, 0, 0, 1, 0, 0 }, UiDrawingValues.ReadMatrix("none"));
    }

    [AvaloniaFact]
    public void Transport_geometry_is_validated_before_any_native_property_changes()
    {
        var snapshot = new UiSessionStore().Publish(new("transport", 0, 1, $"<Path {Ns} ui:Key=\"p\" Data=\"M0,0 L10,10\"/>"), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var original = Assert.IsType<Path>(renderer.Find("/p"));
        var invalid = snapshot with { Revision = snapshot.Revision + 1, Roots = [snapshot.Roots[0] with { Properties = snapshot.Roots[0].Properties.SetItem("Data", J("M0")) }] };
        Assert.Throws<UiException>(() => renderer.Apply(invalid));
        Assert.Same(original, renderer.Find("/p")); Assert.Equal(new Rect(0, 0, 10, 10), original.Data!.Bounds);
    }
}
