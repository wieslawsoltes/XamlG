using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiSelectorTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";
    [Fact]
    public void Grammar_preserves_tree_edges_inheritance_negation_and_position()
    {
        var selector = UiStyles.ParseSelector("StackPanel.scope > :is(Button):not(.muted):nth-child(-n + 3) /template/ Border#chrome");
        Assert.Equal("Border", selector.Target); Assert.Equal("chrome", selector.Name);
        Assert.Equal(3, selector.Steps.Length);
        Assert.Equal(UiSelectorRelation.Child, selector.Steps[1].Relation);
        Assert.Equal(UiSelectorRelation.Template, selector.Steps[2].Relation);
        var button = selector.Steps[1].Predicate; Assert.True(button.IncludeDerived);
        Assert.Equal("muted", Assert.Single(Assert.Single(button.Negations).Classes));
        Assert.Equal(new UiSelectorPosition(-1, 3, false), Assert.Single(button.Positions));
    }
    [Theory]
    [InlineData("")][InlineData("Button >")][InlineData("Button >> Border")]
    [InlineData("Button /unsupported/ Border")][InlineData("Button:not()")]
    [InlineData("Button:not(.a,.b)")][InlineData("Button:nth-child(2n1)")]
    [InlineData("Button:nth-child(2147483647n)")][InlineData("Button:nth-child(n+-1)")]
    [InlineData("Button:nth-child(2):nth-child(3)")][InlineData("Button:is(TextBlock)")]
    [InlineData("Button:nth-child(1 2)")][InlineData("Button:nth-child(o d d)")]
    [InlineData("Button:nth-child(n + 1 2)")][InlineData("Button:nth-child(n - -1)")]
    [InlineData("Button;body")][InlineData("StackPanel > .untyped")]
    public void Invalid_grammar_is_rejected(string source) => Assert.Throws<UiException>(() => UiStyles.ParseSelector(source));
    [AvaloniaFact]
    public void Export_uses_native_positional_and_universal_selector_grammar()
    {
        var source = $"<StackPanel {Ns}><StackPanel.Styles><Style Selector=\"* > TextBlock:nth-child( odd )\"><Setter Property=\"FontSize\" Value=\"23\"/></Style></StackPanel.Styles><TextBlock Text=\"Odd\"/></StackPanel>";
        var snapshot = new UiSessionStore().Publish(new("export-selector", 0, 1, source), "owner");
        var xaml = UiSourceExporter.Xaml(snapshot);
        Assert.Contains(":is(Control)", xaml); Assert.Contains(":nth-child(2n+1)", xaml);
        var root = Assert.IsType<StackPanel>(AvaloniaRuntimeXamlLoader.Load(xaml));
        var window = new Window { Content = root }; window.Show();
        try { Assert.Equal(23, Assert.IsType<TextBlock>(root.Children[0]).FontSize); }
        finally { window.Close(); }
    }
    [Fact]
    public void Groups_and_unused_types_are_validated_before_publication()
    {
        Assert.Equal(new[] { "Button:not(.muted)", "TextBlock" }, UiStyleSelectors.SplitGroups("Button:not(.muted), TextBlock"));
        var store = new UiSessionStore();
        Assert.Throws<UiException>(() => store.Publish(new("bad", 0, 1, $"<StackPanel {Ns}><StackPanel.Styles><Style Selector=\"Window TextBlock\"><Setter Property=\"Opacity\" Value=\"0.5\"/></Style></StackPanel.Styles></StackPanel>"), "owner"));
        Assert.Empty(store.List("owner"));
    }
    [AvaloniaFact]
    public void Logical_child_descendant_and_positional_styles_follow_reparenting_and_local_precedence()
    {
        var store = new UiSessionStore();
        string Source(bool reorder) => $$"""
            <StackPanel {{Ns}} ui:Key="root" Classes="scope">
              <StackPanel.Styles>
                <Style Selector="StackPanel.scope > TextBlock:nth-child(2)"><Setter Property="FontSize" Value="30"/></Style>
                <Style Selector="StackPanel.scope TextBlock:not(.muted)"><Setter Property="Foreground" Value="Blue"/></Style>
              </StackPanel.Styles>
              {{(reorder ? "<TextBlock ui:Key=\"second\" Text=\"second\"/>" : "<TextBlock ui:Key=\"first\" Text=\"first\" Classes=\"muted\"/>")}}
              {{(reorder ? "<TextBlock ui:Key=\"first\" Text=\"first\" Classes=\"muted\"/>" : "<TextBlock ui:Key=\"second\" Text=\"second\"/>")}}
              <Border><TextBlock ui:Key="nested" Text="nested" FontSize="19" Foreground="Red"/></Border>
            </StackPanel>
            """;
        var snapshot = store.Publish(new("selectors", 0, 1, Source(false)), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var window = new Window { Content = renderer.View }; window.Show();
        try
        {
            var first = Assert.IsType<TextBlock>(renderer.Find("/first"));
            var second = Assert.IsType<TextBlock>(renderer.Find("/second"));
            Assert.Equal(30, second.FontSize); Assert.NotEqual(30, first.FontSize);
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(second.Foreground).Color);
            var nested = Assert.IsType<TextBlock>(renderer.Find("/nested")); Assert.Equal(19, nested.FontSize);
            Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(nested.Foreground).Color);
            renderer.Apply(store.Publish(new("selectors", snapshot.Revision, 2, Source(true)), "owner"));
            Assert.Same(first, renderer.Find("/first")); Assert.Same(second, renderer.Find("/second"));
            Assert.Equal(30, first.FontSize); Assert.NotEqual(30, second.FontSize);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void Native_template_parts_and_derived_controls_receive_typed_styles_and_export()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("template-selectors", 0, 1, $$"""
            <StackPanel {{Ns}}>
              <StackPanel.Styles>
                <Style Selector=":is(Button)"><Setter Property="FontSize" Value="23"/></Style>
                <Style Selector="Button /template/ Border#chrome"><Setter Property="Background" Value="Blue"/></Style>
                <Style Selector="TextBlock, Label"><Setter Property="FontSize" Value="21"/></Style>
              </StackPanel.Styles>
              <RepeatButton ui:Key="repeat" Content="Repeat"/>
              <Button ui:Key="button"><Button.Template><ControlTemplate><Border Name="chrome"/></ControlTemplate></Button.Template></Button>
            </StackPanel>
            """), "owner");
        Assert.Equal(4, snapshot.Roots[0].Styles.Length);
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var window = new Window { Content = renderer.View }; window.Show();
        try
        {
            Assert.Equal(23, Assert.IsType<RepeatButton>(renderer.Find("/repeat")).FontSize);
            var button = Assert.IsType<Button>(renderer.Find("/button")); button.ApplyTemplate();
            var border = button.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "chrome");
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
            Assert.IsType<StackPanel>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(snapshot)));
        }
        finally { window.Close(); }
    }
}
