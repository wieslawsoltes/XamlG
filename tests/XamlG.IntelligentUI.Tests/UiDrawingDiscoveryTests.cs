using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using XamlG.Automation;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiDrawingDiscoveryTests
{
    [Fact]
    public async Task Agent_discovery_includes_bounded_grammars_and_a_publishable_drawing()
    {
        var store = new UiSessionStore(); var catalog = new AutomationCatalog();
        using var automation = new UiAutomation(catalog, store);
        var discovery = await catalog.CallAsync("xamlg_ui_catalog", JsonSerializer.SerializeToElement(new { }), new("test", PrincipalId: "owner"));
        var drawing = discovery.GetProperty("drawing");
        Assert.Contains("matrix(", drawing.GetProperty("matrix").GetString());
        Assert.Equal(UiDrawingValues.MaximumPoints, drawing.GetProperty("limits").GetProperty("points").GetInt32());
        Assert.Equal(UiDrawingValues.MaximumPathSegments, drawing.GetProperty("limits").GetProperty("pathSegments").GetInt32());
        var request = discovery.GetProperty("drawingExample").Deserialize<UiPublish>(AutomationJson.Options)!;
        var snapshot = store.Publish(request, "owner");
        Assert.Contains(UiSessionStore.Flatten(snapshot.Roots), node => node.Type == "Path");
        Assert.Contains(UiSessionStore.Flatten(snapshot.Roots), node => node.Type == "LayoutTransformControl");
        Assert.NotEmpty(snapshot.FallbackMarkdown);
    }

    [AvaloniaFact]
    public void Discoverable_drawing_renders_and_exports_to_actual_Avalonia()
    {
        var snapshot = new UiSessionStore().Publish(UiDrawingExamples.LayoutAndDrawing(), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        renderer.View.Measure(new Size(640, 320));
        renderer.View.Arrange(new Rect(0, 0, 640, 320));
        Assert.Equal(new Size(640, 320), Assert.IsType<Grid>(renderer.Find("/viewport")).Bounds.Size);
        Assert.IsType<ScrollViewer>(renderer.Find("/scroll"));
        Assert.IsType<Grid>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(snapshot)));
    }
}
