using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiInputFeatureTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    [AvaloniaTheory]
    [InlineData("CheckBox")][InlineData("ToggleButton")][InlineData("ToggleSwitch")][InlineData("RadioButton")]
    public void Nullable_toggle_values_roundtrip_without_losing_null_or_duplicating_events(string type)
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("toggle", 0, 1, $"<{type} {Ns} ui:Key=\"check\" ui:Bind=\"checked\" IsThreeState=\"True\"/>", J(new { @checked = (bool?)null })), "owner");
        using var renderer = new UiAvaloniaRenderer(); var events = new List<JsonElement>();
        renderer.StateChanged += change => { events.Add(change.Value); renderer.Apply(store.ChangeState(change, "owner")); };
        renderer.Apply(snapshot); var control = Assert.IsAssignableFrom<ToggleButton>(renderer.Find("/check"));
        Assert.Null(control.IsChecked); Assert.Empty(events);
        control.IsChecked = true; control.IsChecked = null; control.IsChecked = false;
        Assert.Equal(new[] { JsonValueKind.True, JsonValueKind.Null, JsonValueKind.False }, events.Select(v => v.ValueKind));
        Assert.False(store.Read("toggle", "owner").State.GetProperty("checked").GetBoolean());
        Assert.Same(control, renderer.Find("/check"));
    }
    [AvaloniaFact]
    public void Keyboard_accessibility_ranges_and_timing_properties_are_native_and_resettable()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("input", 0, 1, $$"""
            <StackPanel {{Ns}} ui:Key="root" KeyboardNavigation.TabNavigation="Cycle">
              <TextBox ui:Key="text" Text="abcdef" CaretIndex="4" SelectionStart="2" SelectionEnd="5"
                TabIndex="3" IsTabStop="False" AutomationProperties.AutomationId="editor" AutomationProperties.HelpText="Enter a name"/>
              <Slider ui:Key="slider" SmallChange="2" LargeChange="15" IsDirectionReversed="True"/>
              <RepeatButton ui:Key="repeat" Delay="500" Interval="40" IsDefault="True" IsCancel="True"/>
            </StackPanel>
            """), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(renderer.Find("/root")!));
        var text = Assert.IsType<TextBox>(renderer.Find("/text"));
        Assert.Equal(2, text.SelectionStart); Assert.Equal(5, text.SelectionEnd);
        Assert.Equal(3, text.TabIndex); Assert.False(text.IsTabStop);
        Assert.Equal("editor", AutomationProperties.GetAutomationId(text));
        Assert.Equal("Enter a name", AutomationProperties.GetHelpText(text));
        var slider = Assert.IsType<Slider>(renderer.Find("/slider")); Assert.Equal(2, slider.SmallChange); Assert.Equal(15, slider.LargeChange); Assert.True(slider.IsDirectionReversed);
        var repeat = Assert.IsType<RepeatButton>(renderer.Find("/repeat")); Assert.Equal(500, repeat.Delay); Assert.Equal(40, repeat.Interval); Assert.True(repeat.IsDefault); Assert.True(repeat.IsCancel);
        var exported = Assert.IsType<StackPanel>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(snapshot)));
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(exported));
        renderer.Apply(store.Publish(new("input", snapshot.Revision, 2, $"<StackPanel {Ns} ui:Key=\"root\"><TextBox ui:Key=\"text\" Text=\"abcdef\"/><Slider ui:Key=\"slider\"/><RepeatButton ui:Key=\"repeat\"/></StackPanel>"), "owner"));
        // Compare with the installed native control rather than a guessed framework default.
        // Avalonia puts an unspecified TabIndex after explicitly indexed controls.
        var defaults = new TextBox();
        Assert.Same(text, renderer.Find("/text")); Assert.Equal(defaults.IsTabStop, text.IsTabStop);
        Assert.Equal(defaults.TabIndex, text.TabIndex);
        Assert.False(text.IsSet(InputElement.TabIndexProperty)); Assert.False(text.IsSet(InputElement.IsTabStopProperty));
        Assert.Null(AutomationProperties.GetAutomationId(text)); Assert.Null(AutomationProperties.GetHelpText(text));
        Assert.Equal(1, slider.SmallChange); Assert.Equal(10, slider.LargeChange); Assert.False(slider.IsDirectionReversed);
        Assert.Equal(300, repeat.Delay); Assert.Equal(100, repeat.Interval); Assert.False(repeat.IsDefault); Assert.False(repeat.IsCancel);
    }
    [AvaloniaFact]
    public void Slider_keyboard_uses_native_small_large_reverse_and_endpoints()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(new("slider", 0, 1,
            $"<Slider {Ns} ui:Key=\"range\" ui:Bind=\"amount\" Minimum=\"0\" Maximum=\"100\" SmallChange=\"2\" LargeChange=\"15\" IsDirectionReversed=\"True\"/>", J(new { amount = 50 })), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.StateChanged += change => renderer.Apply(store.ChangeState(change, "owner")); renderer.Apply(snapshot);
        var slider = Assert.IsType<Slider>(renderer.Find("/range"));
        void KeyPress(Key key) => slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        KeyPress(Key.Right); Assert.Equal(48, slider.Value);
        KeyPress(Key.PageDown); Assert.Equal(63, slider.Value);
        KeyPress(Key.Home); Assert.Equal(0, slider.Value);
        KeyPress(Key.End); Assert.Equal(100, slider.Value);
        Assert.Equal(100, store.Read("slider", "owner").State.GetProperty("amount").GetDecimal());
    }
    [AvaloniaFact]
    public void Selection_is_not_reset_on_an_unrelated_state_echo()
    {
        var store = new UiSessionStore(); var first = store.Publish(new("selection", 0, 1,
            $"<StackPanel {Ns}><TextBox ui:Key=\"edit\" ui:Bind=\"text\" SelectionStart=\"1\" SelectionEnd=\"3\"/><Slider ui:Bind=\"n\"/></StackPanel>", J(new { text = "abcdef", n = 1 })), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(first); var edit = Assert.IsType<TextBox>(renderer.Find("/edit"));
        edit.SelectionStart = 4; edit.SelectionEnd = 5;
        renderer.Apply(store.ChangeState(new(first.Id, first.Revision, 0, "n", J(2)), "owner"));
        Assert.Equal(4, edit.SelectionStart); Assert.Equal(5, edit.SelectionEnd);
    }
    [Fact]
    public void Nullable_template_binding_cannot_target_a_nonnullable_behavior_property()
    {
        var store = new UiSessionStore();
        Assert.Throws<UiException>(() => store.Publish(new("bad", 0, 1,
            $"<CheckBox {Ns}><CheckBox.Template><ControlTemplate><Border IsEnabled=\"{{TemplateBinding IsChecked}}\"/></ControlTemplate></CheckBox.Template></CheckBox>"), "owner"));
    }
    [Theory]
    [InlineData("<TextBox TabIndex=\"-1\"/>")][InlineData("<TextBox SelectionStart=\"16385\"/>")]
    [InlineData("<Slider SmallChange=\"-1\"/>")][InlineData("<RepeatButton Interval=\"0\"/>")]
    [InlineData("<StackPanel KeyboardNavigation.TabNavigation=\"Invalid\"/>")]
    [InlineData("<StackPanel.Styles><Style Selector=\"Button\"><Setter Property=\"IsDefault\" Value=\"True\"/></Style></StackPanel.Styles>")]
    public void Input_behavior_is_typed_bounded_and_not_style_authority(string source) =>
        Assert.Throws<UiException>(() => new UiSessionStore().Publish(new("bad", 0, 1, $"<StackPanel {Ns}>{source}</StackPanel>"), "owner"));
}
