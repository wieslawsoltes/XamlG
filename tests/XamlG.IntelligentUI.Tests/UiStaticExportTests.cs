using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiStaticExportTests
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    [AvaloniaFact]
    public void ExportedItemCollectionsAreRealAvaloniaValuesRatherThanJsonText()
    {
        var request = new UiPublish("items", 0, 1, "<ComboBox xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\" ui:Bind=\"index\" ItemsSource=\"{ui:Expr data.items}\"/>", J(new { index = 1 }), J(new { items = new[] { "First", "Second <&>" } }));
        var snapshot = new UiSessionStore().Publish(request, "owner");
        var xaml = UiSourceExporter.Xaml(snapshot);
        var control = Assert.IsType<ComboBox>(AvaloniaRuntimeXamlLoader.Load(xaml));
        Assert.Equal(2, control.Items.Count); Assert.Equal("Second <&>", control.Items[1]);
        Assert.Equal(1, control.SelectedIndex);
    }
    [AvaloniaFact]
    public void NullableDatesAndThicknessRoundTripThroughAvaloniaXaml()
    {
        var snapshot = new UiSessionStore().Publish(new("date", 0, 1,
            "<DatePicker xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\" Margin=\"1,2,3,4\" ui:Bind=\"date\"/>", J(new { date = (string?)null })), "owner");
        var control = Assert.IsType<DatePicker>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(snapshot)));
        Assert.Null(control.SelectedDate); Assert.Equal(new global::Avalonia.Thickness(1, 2, 3, 4), control.Margin);
    }
    [AvaloniaFact]
    public void EveryDefaultFactoryCanBeExportedAndLoadedByAvalonia()
    {
        foreach (var component in UiCatalog.Default.Components.Values)
        {
            var snapshot = new UiSessionStore().Publish(new("control", 0, 1, $"<{component.Name} xmlns=\"https://github.com/avaloniaui\"/>"), "owner");
            var control = AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(snapshot));
            Assert.Equal(component.Name, control.GetType().Name);
        }
    }
}
