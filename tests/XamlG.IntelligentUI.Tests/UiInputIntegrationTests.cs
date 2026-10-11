using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using XamlG.Automation;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiInputIntegrationTests
{
    [AvaloniaFact]
    public void Discovered_inputs_use_native_selectors_themes_and_revisioned_event_adapters()
    {
        var store = new UiSessionStore(); var first = store.Publish(UiInputExamples.Controls(), "owner");
        using var renderer = new UiAvaloniaRenderer();
        renderer.StateChanged += change => renderer.Apply(store.ChangeState(change, "owner"));
        renderer.ActionRequested += call => renderer.Apply(store.ApplyStateAction(call, "owner"));
        renderer.Apply(first); var window = new Window { Content = renderer.View }; window.Show();
        try
        {
            var name = Assert.IsType<TextBox>(renderer.Find("/name"));
            Assert.Equal("profile-name", AutomationProperties.GetAutomationId(name)); Assert.Equal(0, name.TabIndex);
            var choice = Assert.IsType<CheckBox>(renderer.Find("/choice")); Assert.Null(choice.IsChecked);
            choice.IsChecked = true; choice.IsChecked = null;
            Assert.Equal(JsonValueKind.Null, store.Read(first.Id, "owner").State.GetProperty("choice").ValueKind);
            var slider = Assert.IsType<Slider>(renderer.Find("/amount"));
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            Assert.Equal(48, store.Read(first.Id, "owner").State.GetProperty("amount").GetDecimal());
            var button = Assert.IsType<Button>(renderer.Find("/save")); button.ApplyTemplate();
            var chrome = button.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "chrome");
            Assert.Equal(Colors.Teal, Assert.IsAssignableFrom<ISolidColorBrush>(chrome.BorderBrush).Color);
            Assert.Equal(15, Assert.IsType<RepeatButton>(renderer.Find("/repeat")).FontSize);
            var saved = Assert.IsType<TextBlock>(renderer.Find("/saved-label"));
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(saved.Foreground).Color);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Saved: 1", saved.Text); Assert.Same(name, renderer.Find("/name"));
            var next = store.Read(first.Id, "owner"); Assert.IsType<Grid>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(next)));
            Assert.Contains("Mixed choice", next.FallbackMarkdown);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Discovery_describes_available_selectors_and_inputs_without_removing_existing_examples()
    {
        var tools = new AutomationCatalog(); var store = new UiSessionStore(); using var automation = new UiAutomation(tools, store);
        var catalog = await tools.CallAsync("xamlg_ui_catalog", JsonSerializer.SerializeToElement(new { }), new("test", PrincipalId: "owner"));
        Assert.Equal(UiStyleSelectors.MaximumSteps, catalog.GetProperty("authoring").GetProperty("selectors").GetProperty("maximumSteps").GetInt32());
        Assert.Equal(6, catalog.GetProperty("authoring").GetProperty("inputs").GetProperty("navigationModes").GetArrayLength());
        foreach (var field in new[] { "example", "localActions", "dashboard", "form", "drawingExample", "authoringExample", "inputExample" })
        {
            var request = catalog.GetProperty(field).Deserialize<UiPublish>(AutomationJson.Options)!;
            Assert.NotEmpty(store.Publish(request, "owner").Roots);
        }
    }
}
