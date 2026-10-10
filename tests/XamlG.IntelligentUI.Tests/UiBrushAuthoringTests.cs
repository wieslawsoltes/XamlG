using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiBrushAuthoringTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private static UiPublish Source(string body, object? data = null) => new("brushes", 0, 1, $"<StackPanel {Ns}>{body}</StackPanel>", Data: J(data ?? new { }));
    [AvaloniaFact]
    public void Inline_resource_brushes_preserve_geometry_opacity_spread_and_export()
    {
        var snapshot = new UiSessionStore().Publish(Source("""
            <StackPanel.Resources>
              <LinearGradientBrush x:Key="accent" StartPoint="0%,50%" EndPoint="100%,50%" SpreadMethod="Reflect" Opacity="0.6">
                <LinearGradientBrush.Transform><MatrixTransform Matrix="1,0,0,1,3,4"/></LinearGradientBrush.Transform>
                <LinearGradientBrush.GradientStops><GradientStop Color="Red" Offset="0"/><GradientStop Color="#800000FF" Offset="1"/></LinearGradientBrush.GradientStops>
              </LinearGradientBrush>
              <RadialGradientBrush x:Key="radial" Center="40%,60%" GradientOrigin="25%,35%" RadiusX="60%" RadiusY="30%">
                <GradientStop Color="White" Offset="0"/><GradientStop Color="Blue" Offset="1"/>
              </RadialGradientBrush>
            </StackPanel.Resources>
            <Border ui:Key="border" Background="{StaticResource accent}"/>
            <Ellipse ui:Key="ellipse" Fill="{StaticResource radial}"/>
            <TextBlock ui:Key="text" Text="Opacity"><TextBlock.Foreground><SolidColorBrush Color="Red" Opacity="0.25"/></TextBlock.Foreground></TextBlock>
            """), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var linear = Assert.IsType<LinearGradientBrush>(Assert.IsType<Border>(renderer.Find("/border")).Background);
        Assert.Equal(.6, linear.Opacity); Assert.Equal(GradientSpreadMethod.Reflect, linear.SpreadMethod); Assert.Equal(2, linear.GradientStops.Count);
        Assert.Equal(new RelativePoint(0, .5, RelativeUnit.Relative), linear.StartPoint); Assert.Equal(3, linear.Transform!.Value.M31);
        var radial = Assert.IsType<RadialGradientBrush>(Assert.IsType<Ellipse>(renderer.Find("/ellipse")).Fill);
        Assert.Equal(new RelativeScalar(.6, RelativeUnit.Relative), radial.RadiusX); Assert.Equal(new RelativeScalar(.3, RelativeUnit.Relative), radial.RadiusY);
        Assert.Equal(.25, Assert.IsType<SolidColorBrush>(Assert.IsType<TextBlock>(renderer.Find("/text")).Foreground).Opacity);
        var xaml = UiSourceExporter.Xaml(snapshot); Assert.Contains("Border.Background", xaml); Assert.Contains("GradientStop", xaml);
        var root = Assert.IsType<StackPanel>(AvaloniaRuntimeXamlLoader.Load(xaml));
        var exported = Assert.IsType<LinearGradientBrush>(Assert.IsType<Border>(root.Children[0]).Background);
        Assert.Equal(linear.Opacity, exported.Opacity); Assert.Equal(linear.Transform.Value, exported.Transform!.Value);
        Assert.Equal(radial.RadiusY, Assert.IsType<RadialGradientBrush>(Assert.IsType<Ellipse>(root.Children[1]).Fill).RadiusY);
    }
    [AvaloniaFact]
    public void Bound_brush_replacement_is_transactional_and_retains_controls()
    {
        var store = new UiSessionStore();
        var first = store.Publish(Source("<Border ui:Key=\"border\" Background=\"{Binding paint}\"/>", new { paint = new { kind = "linear", stops = new[] { new { color = "Red", offset = 0 }, new { color = "Blue", offset = 1 } } } }), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(first); var border = Assert.IsType<Border>(renderer.Find("/border"));
        Assert.IsType<LinearGradientBrush>(border.Background);
        Assert.Throws<UiException>(() => store.ChangeData(new(first.Id, first.Revision, J(new { paint = new { kind = "linear", stops = new[] { new { color = "Red", offset = 2 } } } })), "owner"));
        Assert.Same(first, store.Read(first.Id, "owner"));
        var next = store.ChangeData(new(first.Id, first.Revision, J(new { paint = "Green" })), "owner"); renderer.Apply(next);
        Assert.Same(border, renderer.Find("/border")); Assert.Equal(Colors.Green, Assert.IsType<SolidColorBrush>(border.Background).Color);
        next = store.ChangeData(new(next.Id, next.Revision, J(new { paint = (object?)null })), "owner"); renderer.Apply(next); Assert.Null(border.Background);
    }
    [AvaloniaFact]
    public void Gradient_styles_obey_local_precedence_and_export_as_real_setter_values()
    {
        var snapshot = new UiSessionStore().Publish(Source("""
            <StackPanel.Styles><Style Selector="Border.accent"><Setter Property="Background"><Setter.Value>
              <LinearGradientBrush><GradientStop Color="Red" Offset="0"/><GradientStop Color="Blue" Offset="1"/></LinearGradientBrush>
            </Setter.Value></Setter></Style></StackPanel.Styles>
            <Border ui:Key="style" Classes="accent"/><Border ui:Key="local" Classes="accent" Background="Green"/>
            """), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var window = new Window { Content = renderer.View }; window.Show();
        try
        {
            Assert.IsType<LinearGradientBrush>(Assert.IsType<Border>(renderer.Find("/style")).Background);
            Assert.Equal(Colors.Green, Assert.IsAssignableFrom<ISolidColorBrush>(Assert.IsType<Border>(renderer.Find("/local")).Background).Color);
            Assert.Contains("Setter.Value", UiSourceExporter.Xaml(snapshot)); Assert.IsType<StackPanel>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(snapshot)));
        }
        finally { window.Close(); }
    }
    [Theory]
    [InlineData("{\"kind\":\"image\",\"uri\":\"file:///private\"}")]
    [InlineData("{\"kind\":\"solid\",\"color\":\"url(https://example.com)\"}")]
    [InlineData("{\"kind\":\"solid\",\"opacity\":2}")]
    [InlineData("{\"kind\":\"solid\",\"kind\":\"solid\"}")]
    [InlineData("{\"kind\":\"linear\",\"stops\":[{\"offset\":1,\"color\":\"Red\"},{\"offset\":0,\"color\":\"Blue\"}]}")]
    [InlineData("{\"kind\":\"radial\",\"radiusX\":\"NaN\",\"stops\":[]}")]
    [InlineData("{\"kind\":\"linear\",\"stops\":[],\"center\":\"50%,50%\"}")]
    public void Untrusted_brush_descriptors_are_rejected(string source) => Assert.Throws<UiException>(() => UiBrushValues.ParseLiteral(source));
    [Fact]
    public void Excessive_stops_and_conflicting_property_elements_are_rejected()
    {
        var tooMany = J(new { kind = "linear", stops = Enumerable.Range(0, 65).Select(i => new { color = "Red", offset = i / 64d }).ToArray() });
        Assert.Throws<UiException>(() => UiBrushValues.Read(tooMany));
        Assert.Throws<UiException>(() => new UiSessionStore().Publish(Source("<Border><Border.Background><LinearGradientBrush><GradientStop Color=\"Red\"/><LinearGradientBrush.GradientStops/></LinearGradientBrush></Border.Background></Border>"), "owner"));
    }
}
