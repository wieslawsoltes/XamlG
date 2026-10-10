using System.Collections.Immutable;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiAuthoringTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private static UiSnapshot Publish(string body) => new UiSessionStore().Publish(new("authoring", 0, 1, $"<StackPanel {Ns}>{body}</StackPanel>"), "owner");

    [AvaloniaFact]
    public void Property_elements_resources_styles_and_native_local_precedence_work_together()
    {
        var snapshot = Publish("""
            <StackPanel.Resources><SolidColorBrush x:Key="accent" Color="Blue"/><x:Double x:Key="size">23</x:Double></StackPanel.Resources>
            <StackPanel.Styles><Style Selector="TextBlock.accent"><Setter Property="Foreground" Value="{StaticResource accent}"/><Setter Property="FontSize" Value="{StaticResource size}"/></Style></StackPanel.Styles>
            <TextBlock x:Name="styled" Classes="accent"><TextBlock.Text>Styled text</TextBlock.Text></TextBlock>
            <TextBlock x:Name="local" Classes="accent" Foreground="Red"/>
            <Border ui:Key="border"><Border.Child><TextBlock Text="Content"/></Border.Child></Border>
            """);
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var window = new Window { Content = renderer.View }; window.Show();
        try
        {
            var styled = Assert.IsType<TextBlock>(renderer.Find("/styled"));
            Assert.Equal("Styled text", styled.Text); Assert.Equal(23, styled.FontSize);
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(styled.Foreground).Color);
            Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(Assert.IsType<TextBlock>(renderer.Find("/local")).Foreground).Color);
            Assert.IsType<TextBlock>(Assert.IsType<Border>(renderer.Find("/border")).Child);
            var exported = UiSourceExporter.Xaml(snapshot);
            Assert.Contains("StackPanel.Styles", exported); Assert.IsType<StackPanel>(AvaloniaRuntimeXamlLoader.Load(exported));
        }
        finally { window.Close(); }
    }
    [Fact]
    public void Resources_use_lexical_scope_and_merged_dictionary_precedence()
    {
        var snapshot = Publish("""
            <StackPanel.Resources><ResourceDictionary>
              <ResourceDictionary.MergedDictionaries><ResourceDictionary><x:String x:Key="value">merged</x:String></ResourceDictionary></ResourceDictionary.MergedDictionaries>
              <x:String x:Key="value">outer</x:String><StaticResource x:Key="alias" ResourceKey="value"/>
            </ResourceDictionary></StackPanel.Resources>
            <StackPanel><StackPanel.Resources><x:String x:Key="value">inner</x:String></StackPanel.Resources>
              <TextBlock ui:Key="local" Text="{StaticResource value}"/><TextBlock ui:Key="alias" Text="{StaticResource alias}"/>
            </StackPanel>
            """);
        var nodes = UiSessionStore.Flatten(snapshot.Roots).ToDictionary(n => n.Key);
        Assert.Equal("inner", nodes["/local"].Properties["Text"].GetString());
        Assert.Equal("outer", nodes["/alias"].Properties["Text"].GetString());
    }
    [AvaloniaFact]
    public void Data_templates_repeat_with_stable_identity_context_and_reuse()
    {
        var store = new UiSessionStore();
        var source = $$"""
            <ItemsControl {{Ns}} ui:Key="rows" ItemsSource="{ui:Expr data.rows}" ui:ItemKey="{ui:Expr item.id}">
              <ItemsControl.Resources><DataTemplate x:Key="row"><Button Content="{ui:Expr item.title}" ui:Action="choose"/></DataTemplate></ItemsControl.Resources>
              <ItemsControl.ItemTemplate><StaticResource ResourceKey="row"/></ItemsControl.ItemTemplate>
            </ItemsControl>
            """;
        var data = J(new { rows = new[] { new { id = "a", title = "Alpha" }, new { id = "b", title = "Beta" } } });
        var first = store.Publish(new("rows", 0, 1, source, J(new { chosen = "" }), data,
            [new("choose", "state", Arguments: J(new { chosen = "{ui:Expr item.id}" }))]), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(first);
        var nodes = UiSessionStore.Flatten(first.Roots).Where(n => n.Type == "Button").ToArray();
        var alpha = renderer.Find(nodes[0].Key);
        var next = store.ChangeData(new(first.Id, first.Revision, J(new { rows = new[] { new { id = "b", title = "Beta" }, new { id = "a", title = "Changed" } } })), "owner");
        renderer.Apply(next); Assert.Same(alpha, renderer.Find(nodes[0].Key));
        Assert.Equal("Changed", Assert.IsType<Button>(alpha).Content);
        var action = store.ApplyStateAction(new(next.Id, next.Revision, next.StateRevision, nodes[0].Key), "owner");
        Assert.Equal("a", action.State.GetProperty("chosen").GetString());
    }
    [Fact]
    public void Inline_templates_and_literal_string_arrays_share_the_repeat_pipeline()
    {
        var snapshot = Publish("""
            <ItemsControl ItemsSource="[&quot;A&quot;,&quot;B&quot;]"><ItemsControl.ItemTemplate>
              <DataTemplate><TextBlock Text="{ui:Expr item}"/></DataTemplate>
            </ItemsControl.ItemTemplate></ItemsControl>
            """);
        Assert.Equal(new[] { "A", "B" }, UiSessionStore.Flatten(snapshot.Roots).Where(n => n.Type == "TextBlock").Select(n => n.Properties["Text"].GetString()));
    }
    [Fact]
    public void Reactive_resource_and_style_values_revalidate_transactionally()
    {
        var store = new UiSessionStore();
        var source = $$"""
            <StackPanel {{Ns}}><StackPanel.Resources><x:Double x:Key="size">{ui:Expr state.size}</x:Double></StackPanel.Resources>
              <StackPanel.Styles><Style Selector="TextBlock"><Setter Property="FontSize" Value="{DynamicResource size}"/></Style></StackPanel.Styles>
              <Slider ui:Bind="size" Minimum="1" Maximum="1000"/><TextBlock Text="Styled"/>
            </StackPanel>
            """;
        var initial = store.Publish(new("reactive", 0, 1, source, J(new { size = 20 })), "owner");
        Assert.Equal(20, initial.Roots[0].Styles[0].Properties["FontSize"].GetDecimal());
        var next = store.ChangeState(new(initial.Id, initial.Revision, 0, "size", J(24)), "owner");
        Assert.Equal(24, next.Roots[0].Styles[0].Properties["FontSize"].GetDecimal());
        Assert.Throws<UiException>(() => store.ChangeState(new(next.Id, next.Revision, next.StateRevision, "size", J(900)), "owner"));
        Assert.Same(next, store.Read(next.Id, "owner"));
    }
    [Theory]
    [InlineData("<TextBlock Text=\"a\"><TextBlock.Text>b</TextBlock.Text></TextBlock>")]
    [InlineData("<Border><Button/><Border.Child><TextBlock/></Border.Child></Border>")]
    [InlineData("<StackPanel.Resources><StaticResource x:Key=\"a\" ResourceKey=\"b\"/><StaticResource x:Key=\"b\" ResourceKey=\"a\"/></StackPanel.Resources>")]
    [InlineData("<TextBlock Text=\"{StaticResource missing}\"/>")]
    [InlineData("<StackPanel.Resources><x:String x:Key=\"a\">1</x:String><x:String x:Key=\"a\">2</x:String></StackPanel.Resources>")]
    [InlineData("<StackPanel.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceInclude Source=\"https://example.com/a\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></StackPanel.Resources>")]
    [InlineData("<StackPanel.Styles><Style Selector=\"Button\"><Setter Property=\"IsEnabled\" Value=\"False\"/></Style></StackPanel.Styles>")]
    [InlineData("<StackPanel.Styles><Style Selector=\"Slider\"><Setter Property=\"Value\" Value=\"2\"/></Style></StackPanel.Styles>")]
    [InlineData("<StackPanel.Styles><Style Selector=\"Button /template/ Border\"><Setter Property=\"Opacity\" Value=\"0\"/></Style></StackPanel.Styles>")]
    [InlineData("<TextBlock Classes=\":pressed\"/>")]
    [InlineData("<ItemsControl ItemsSource=\"[]\"><ItemsControl.ItemTemplate><DataTemplate><Window/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>")]
    public void Invalid_authoring_never_publishes(string source) => Assert.Throws<UiException>(() => Publish(source));

    [Fact]
    public void Nested_style_selectors_preserve_framework_pseudoclasses()
    {
        var snapshot = Publish("""
            <StackPanel.Styles><Style Selector="Button.primary"><Setter Property="Background" Value="Blue"/>
              <Style Selector="^:pointerover"><Setter Property="Background" Value="Red"/></Style>
            </Style></StackPanel.Styles><Button Classes="primary"/>
            """);
        Assert.Equal("Button.primary:pointerover", snapshot.Roots[0].Styles[1].Selector);
        Assert.Equal(new[] { "pointerover" }, UiStyles.ParseSelector(snapshot.Roots[0].Styles[1].Selector).PseudoClasses);
    }
    [AvaloniaFact]
    public void Transport_styles_are_validated_before_native_mutation()
    {
        var snapshot = Publish("<TextBlock ui:Key=\"text\" Text=\"Good\"/>");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot); var control = renderer.Find("/text");
        var bad = snapshot with { Roots = [snapshot.Roots[0] with { Styles = [new("TextBlock", ImmutableDictionary<string, JsonElement>.Empty.Add("FontSize", J(-1)))] }] };
        Assert.Throws<UiException>(() => renderer.Apply(bad)); Assert.Same(control, renderer.Find("/text"));
    }
}
