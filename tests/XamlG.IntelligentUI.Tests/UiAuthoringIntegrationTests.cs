using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using XamlG.Automation;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiAuthoringIntegrationTests
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    [AvaloniaFact]
    public void Authoring_example_keeps_templates_reactive_and_round_trips_native_export()
    {
        var store = new UiSessionStore(); var first = store.Publish(UiAuthoringExamples.Directory(), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(first);
        Assert.Equal(3, UiSessionStore.Flatten(first.Roots).Count(node => node.Type == "Button"));
        var button = UiSessionStore.Flatten(first.Roots).First(node => node.Type == "Button");
        var reference = renderer.Find(button.Key);
        var next = store.ChangeState(new(first.Id, first.Revision, first.StateRevision, "minimum", J(90)), "owner");
        renderer.Apply(next); Assert.Same(reference, renderer.Find(button.Key));
        Assert.Equal(2, UiSessionStore.Flatten(next.Roots).Count(node => node.Type == "Button"));
        next = store.ApplyStateAction(new(next.Id, next.Revision, next.StateRevision, button.Key), "owner");
        Assert.Equal("featured", next.State.GetProperty("selected").GetString());
        Assert.IsType<Grid>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(next)));
    }
    [Fact]
    public async Task Agent_discovery_exposes_executable_authoring_example_and_limits()
    {
        var tools = new AutomationCatalog(); var store = new UiSessionStore(); using var automation = new UiAutomation(tools, store);
        var result = await tools.CallAsync("xamlg_ui_catalog", J(new { }), new("test", PrincipalId: "owner"));
        var request = result.GetProperty("authoringExample").Deserialize<UiPublish>(AutomationJson.Options)!;
        Assert.NotEmpty(store.Publish(request, "owner").Roots);
        Assert.Equal(64, result.GetProperty("authoring").GetProperty("brushes").GetProperty("maximumStops").GetInt32());
    }
    [AvaloniaFact]
    public void Explicit_null_style_brush_exports_as_null_not_a_color_named_null()
    {
        var store = new UiSessionStore();
        var first = store.Publish(new("null-brush", 0, 1, """
            <StackPanel xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel.Styles><Style Selector="Border"><Setter Property="Background"><Setter.Value><x:Null/></Setter.Value></Setter></Style></StackPanel.Styles>
              <Border/>
            </StackPanel>
            """), "owner");
        var xaml = UiSourceExporter.Xaml(first); Assert.Contains("x:Null", xaml);
        Assert.IsType<StackPanel>(AvaloniaRuntimeXamlLoader.Load(xaml));
    }
    [Fact]
    public void Quoted_path_keys_can_contain_binding_option_separators()
    {
        var first = new UiSessionStore().Publish(new("keys", 0, 1, """
            <TextBlock xmlns="https://github.com/avaloniaui" Text="{Binding labels['a=b,c'], FallbackValue='missing'}"/>
            """, Data: J(new { labels = new Dictionary<string, string> { ["a=b,c"] = "Present" } })), "owner");
        Assert.Equal("Present", first.Roots[0].Properties["Text"].GetString());
    }
    [Theory]
    [InlineData("1e-320")][InlineData("0")][InlineData("-1")][InlineData("1e100")]
    public void Radius_bounds_prevent_subnormal_division_and_infinite_elliptical_transforms(string radius)
        => Assert.Throws<UiException>(() => UiBrushValues.Read(J(new { kind = "radial", radiusX = radius, stops = Array.Empty<object>() })));

}
