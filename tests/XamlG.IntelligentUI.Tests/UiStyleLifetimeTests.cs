using System.Collections.Immutable;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiStyleLifetimeTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";
    [AvaloniaFact]
    public void Unnamed_controls_retain_identity_and_name_changes_replace_only_the_named_control()
    {
        var store = new UiSessionStore();
        string Source(string name, string text) => $"<StackPanel {Ns} ui:Key=\"root\"><TextBlock ui:Key=\"text\" {name} Text=\"{text}\"/></StackPanel>";
        var snapshot = store.Publish(new("identity", 0, 1, Source("", "one")), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var root = renderer.Find("/root"); var text = renderer.Find("/text");
        snapshot = store.Publish(new("identity", snapshot.Revision, 2, Source("", "two")), "owner"); renderer.Apply(snapshot);
        Assert.Same(root, renderer.Find("/root")); Assert.Same(text, renderer.Find("/text"));
        snapshot = store.Publish(new("identity", snapshot.Revision, 3, Source("Name=\"named\"", "three")), "owner"); renderer.Apply(snapshot);
        Assert.Same(root, renderer.Find("/root")); Assert.NotSame(text, renderer.Find("/text"));
        var named = renderer.Find("/text"); Assert.Equal("named", named!.Name);
        snapshot = store.Publish(new("identity", snapshot.Revision, 4, Source("", "four")), "owner"); renderer.Apply(snapshot);
        Assert.NotSame(named, renderer.Find("/text")); Assert.Null(renderer.Find("/text")!.Name);
    }
    [AvaloniaFact]
    public void Removing_styles_restores_framework_values_and_keeps_retained_controls()
    {
        var store = new UiSessionStore();
        string Source(string styles, string classes) => $"<StackPanel {Ns} ui:Key=\"root\">{styles}<TextBlock ui:Key=\"text\" Classes=\"{classes}\" Text=\"Styled\"/></StackPanel>";
        const string styles = "<StackPanel.Styles><Style Selector=\"TextBlock.accent\"><Setter Property=\"FontSize\" Value=\"29\"/><Setter Property=\"Foreground\" Value=\"Blue\"/></Style></StackPanel.Styles>";
        var snapshot = store.Publish(new("style-life", 0, 1, Source(styles, "accent")), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var window = new Window { Content = renderer.View }; window.Show();
        try
        {
            var text = Assert.IsType<TextBlock>(renderer.Find("/text")); Assert.Equal(29, text.FontSize);
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
            snapshot = store.Publish(new("style-life", snapshot.Revision, 2, Source(styles, "")), "owner"); renderer.Apply(snapshot);
            Assert.Same(text, renderer.Find("/text")); Assert.NotEqual(29, text.FontSize);
            snapshot = store.Publish(new("style-life", snapshot.Revision, 3, Source(styles, "accent")), "owner"); renderer.Apply(snapshot);
            Assert.Equal(29, text.FontSize);
            snapshot = store.Publish(new("style-life", snapshot.Revision, 4, Source("", "accent")), "owner"); renderer.Apply(snapshot);
            Assert.Same(text, renderer.Find("/text")); Assert.NotEqual(29, text.FontSize);
            Assert.Empty(Assert.IsType<StackPanel>(renderer.Find("/root")).Styles);
        }
        finally { window.Close(); }
    }
    [Fact]
    public void Whole_tree_style_budget_cannot_be_multiplied_by_repetition()
    {
        var property = ImmutableDictionary<string, JsonElement>.Empty.Add("Opacity", JsonSerializer.SerializeToElement(.5));
        var rule = new UiStyleRule("TextBlock", property);
        var roots = Enumerable.Range(0, 5).Select(i => new UiElement(i.ToString(), "StackPanel", ImmutableDictionary<string, JsonElement>.Empty, [])
            { Styles = Enumerable.Repeat(rule, 128).ToImmutableArray() }).ToArray();
        Assert.Equal("invalid_style", Assert.Throws<UiException>(() => UiStyleTree.Validate(roots)).Code);
    }
}
