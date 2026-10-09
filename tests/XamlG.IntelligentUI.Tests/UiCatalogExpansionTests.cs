using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiCatalogExpansionTests
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";
    [AvaloniaFact] public void EveryDefaultComponentHasAnExactTypedFactoryAndPropertyCatalog()
    {
        Assert.True(UiCatalog.Default.Components.Count >= 40);
        Assert.Equal(UiCatalog.Default.Components.Keys.Order(), UiAvaloniaCatalog.Default.Registrations.Keys.Order());
        foreach (var component in UiCatalog.Default.Components.Values)
        {
            var registration = UiAvaloniaCatalog.Default.Registrations[component.Name];
            Assert.Equal(component.Properties.Keys.Order(), registration.Setters.Keys.Order());
            Assert.NotNull(registration.Create());
            if (component.InputProperty != null) Assert.NotNull(registration.ReadInput);
        }
    }
    [AvaloniaFact] public void SelectionInputsPreserveItemsAndUpdateReactiveState()
    {
        var store = new UiSessionStore();
        var first = store.Publish(new("selection", 0, 1, $"<StackPanel {Ns}><ComboBox ui:Key=\"combo\" ui:Bind=\"selected\" ItemsSource=\"{{ui:Expr data.names}}\"/><TextBlock Text=\"{{ui:Expr data.names[state.selected]}}\"/></StackPanel>", J(new { selected = 1 }), J(new { names = new[] { "Alpha", "Beta", "Gamma" } })), "owner");
        using var renderer = new UiAvaloniaRenderer();
        renderer.StateChanged += change => renderer.Apply(store.ChangeState(change, "owner")); renderer.Apply(first);
        var combo = Assert.IsType<ComboBox>(renderer.Find("/combo")); Assert.Equal(1, combo.SelectedIndex); Assert.Equal(3, combo.ItemCount);
        combo.SelectedIndex = 2;
        Assert.Equal(2, store.Read("selection", "owner").State.GetProperty("selected").GetInt32());
        Assert.Contains("Gamma", store.Read("selection", "owner").FallbackMarkdown);
        Assert.Same(combo, renderer.Find("/combo"));
    }
    [AvaloniaFact] public void TabsAndContentContainersRetainKeyedChildren()
    {
        var store = new UiSessionStore();
        var request = new UiPublish("tabs", 0, 1, $"<TabControl {Ns} ui:Key=\"tabs\" ui:Bind=\"tab\"><TabItem Header=\"A\"><Border Padding=\"4,8\"><TextBlock ui:Key=\"text\" Text=\"Ready\"/></Border></TabItem><TabItem Header=\"B\"><Button Content=\"Next\"/></TabItem></TabControl>", J(new { tab = 1 }));
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(store.Publish(request, "owner"));
        var tabs = Assert.IsType<TabControl>(renderer.Find("/tabs")); Assert.Equal(2, tabs.ItemCount); Assert.Equal(1, tabs.SelectedIndex);
        var text = renderer.Find("/text"); renderer.Apply(store.Publish(request with { ExpectedRevision = 1, Sequence = 2 }, "owner"));
        Assert.Same(text, renderer.Find("/text")); Assert.Equal(1, tabs.SelectedIndex);
    }
    [AvaloniaFact] public void NumericAndToggleInputsRoundTripWithoutNativeEventLoops()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("inputs", 0, 1, $"<StackPanel {Ns}><NumericUpDown ui:Key=\"amount\" ui:Bind=\"amount\" Minimum=\"0\" Maximum=\"100\" Increment=\"0.5\" FormatString=\"F2\"/><ToggleSwitch ui:Key=\"enabled\" ui:Bind=\"enabled\"/></StackPanel>", J(new { amount = 2.5m, enabled = true })), "owner");
        using var renderer = new UiAvaloniaRenderer(); var changes = 0;
        renderer.StateChanged += change => { changes++; renderer.Apply(store.ChangeState(change, "owner")); }; renderer.Apply(snapshot);
        Assert.Equal(0, changes);
        Assert.IsType<NumericUpDown>(renderer.Find("/amount")).Value = 7.5m;
        Assert.IsType<ToggleSwitch>(renderer.Find("/enabled")).IsChecked = false;
        Assert.Equal(2, changes); Assert.Equal(7.5m, store.Read("inputs", "owner").State.GetProperty("amount").GetDecimal());
    }
    [Theory]
    [InlineData("<TabControl><Button/></TabControl>")]
    [InlineData("<ComboBox ItemsSource=\"[&quot;A&quot;]\" SelectedIndex=\"3\"/>")]
    [InlineData("<NumericUpDown FormatString=\"F999999999\"/>")]
    [InlineData("<Border Padding=\"1,2,3\"/>")]
    [InlineData("<Button Content=\"A\"><TextBlock Text=\"B\"/></Button>")]
    public void RejectsConflictingOrUnboundedNativeValues(string children)
    {
        Assert.Throws<UiException>(() => new UiSessionStore().Publish(new("bad", 0, 1, $"<StackPanel {Ns}>{children}</StackPanel>"), "owner"));
    }
    [AvaloniaFact] public void DateAndTimeUseInvariantTypedLiterals()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("dates", 0, 1, $"<StackPanel {Ns}><DatePicker ui:Key=\"date\" SelectedDate=\"2026-10-09\"/><TimePicker ui:Key=\"time\" SelectedTime=\"13:45:00\"/></StackPanel>"), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        Assert.Equal(2026, Assert.IsType<DatePicker>(renderer.Find("/date")).SelectedDate!.Value.Year);
        Assert.Equal(TimeSpan.FromHours(13.75), Assert.IsType<TimePicker>(renderer.Find("/time")).SelectedTime);
    }
}
