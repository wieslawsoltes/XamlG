using System.Collections.Immutable;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiControlTemplateTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string Template = """
        <Button.Template><ControlTemplate>
          <Border Name="chrome" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
            <ContentPresenter Name="PART_ContentPresenter" Content="{TemplateBinding Content}"/>
          </Border>
        </ControlTemplate></Button.Template>
        """;
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    [AvaloniaFact]
    public void Native_template_bindings_track_owner_and_preserve_response_content()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("template", 0, 1, $"<Button {Ns} ui:Key=\"button\" Background=\"Blue\" Padding=\"8\">{Template}<TextBlock ui:Key=\"content\" Text=\"Shared content\"/></Button>"), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var button = Assert.IsType<Button>(renderer.Find("/button"));
        var window = new Window { Content = renderer.View }; window.Show(); button.ApplyTemplate();
        try
        {
            var chrome = button.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "chrome");
            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single(c => c.Name == "PART_ContentPresenter");
            Assert.Same(renderer.Find("/content"), presenter.Content);
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(chrome.Background).Color);
            button.Background = Brushes.Red;
            Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(chrome.Background).Color);
            var replacement = store.Publish(new("template", snapshot.Revision, 2, $"<Button {Ns} ui:Key=\"button\"><TextBlock ui:Key=\"content\" Text=\"Still retained\"/></Button>"), "owner");
            var child = renderer.Find("/content"); renderer.Apply(replacement); button.ApplyTemplate();
            Assert.Same(button, renderer.Find("/button")); Assert.Same(child, renderer.Find("/content")); Assert.Same(child, button.Content);
            Assert.Null(chrome.Child);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void Two_way_template_input_updates_the_registered_owner_once()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("slider", 0, 1, $$"""
            <Slider {{Ns}} ui:Key="owner" ui:Bind="value" Minimum="0" Maximum="100">
              <Slider.Template><ControlTemplate><Slider Name="input" Minimum="0" Maximum="100" Value="{TemplateBinding Value, Mode=TwoWay}"/></ControlTemplate></Slider.Template>
            </Slider>
            """, J(new { value = 15 })), "owner");
        using var renderer = new UiAvaloniaRenderer(); var changes = 0;
        renderer.StateChanged += change => { changes++; renderer.Apply(store.ChangeState(change, "owner")); }; renderer.Apply(snapshot);
        var owner = Assert.IsType<Slider>(renderer.Find("/owner")); var window = new Window { Content = renderer.View }; window.Show(); owner.ApplyTemplate();
        try
        {
            var input = owner.GetVisualDescendants().OfType<Slider>().Single(c => c.Name == "input");
            Assert.Equal(15, input.Value); input.Value = 37;
            Assert.Equal(37, owner.Value); Assert.Equal(37, store.Read("slider", "owner").State.GetProperty("value").GetInt32());
            Assert.Equal(1, changes); Assert.Same(input, owner.GetVisualDescendants().OfType<Slider>().Single(c => c.Name == "input"));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void Control_theme_inheritance_and_pseudoclasses_use_native_precedence_and_export()
    {
        var source = $$"""
            <StackPanel {{Ns}}>
              <StackPanel.Resources>
                <ControlTheme x:Key="base" TargetType="Button"><Setter Property="Background" Value="Blue"/><Setter Property="Padding" Value="7"/></ControlTheme>
                <ControlTheme x:Key="derived" TargetType="Button" BasedOn="{StaticResource base}">
                  <Setter Property="Template"><Setter.Value><ControlTemplate><Border Name="chrome" Background="{TemplateBinding Background}"><ContentPresenter Content="{TemplateBinding Content}"/></Border></ControlTemplate></Setter.Value></Setter>
                  <Style Selector="^:disabled"><Setter Property="Background" Value="Gray"/></Style>
                </ControlTheme>
              </StackPanel.Resources>
              <Button ui:Key="button" Content="Themed" Theme="{StaticResource derived}"/>
            </StackPanel>
            """;
        var store = new UiSessionStore(); var snapshot = store.Publish(new("theme", 0, 1, source), "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(snapshot);
        var window = new Window { Content = renderer.View }; window.Show();
        try
        {
            var button = Assert.IsType<Button>(renderer.Find("/button")); button.ApplyTemplate();
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
            button.IsEnabled = false; Assert.Equal(Colors.Gray, Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
            button.Background = Brushes.Red; Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
            var xaml = UiSourceExporter.Xaml(snapshot); Assert.Contains("ControlTheme", xaml); Assert.Contains("TemplateBinding", xaml);
            Assert.IsType<StackPanel>(AvaloniaRuntimeXamlLoader.Load(xaml));
        }
        finally { window.Close(); }
    }
    [Theory]
    [InlineData("<Button.Template><ControlTemplate><Border Width=\"{TemplateBinding Missing}\"/></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Template><ControlTemplate><Border Width=\"{TemplateBinding Content}\"/></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Template><ControlTemplate><Border Width=\"{TemplateBinding Width, Mode=TwoWay}\"/></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Template><ControlTemplate><Border Width=\"{TemplateBinding Width, Mode=OneTime}\"/></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Template><ControlTemplate TargetType=\"Slider\"><Border/></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Template><ControlTemplate><StackPanel><Border Name=\"same\"/><Border Name=\"same\"/></StackPanel></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Template><ControlTemplate><TextBox ui:Bind=\"secret\"/></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Template><ControlTemplate><Button ui:Action=\"secret\"/></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Template><ControlTemplate><ItemsPresenter/></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Template><ControlTemplate><ContentPresenter Content=\"{TemplateBinding Content}\"><TextBlock/></ContentPresenter></ControlTemplate></Button.Template>")]
    [InlineData("<Button.Theme><ControlTheme TargetType=\"Button\"><Setter Property=\"IsEnabled\" Value=\"False\"/></ControlTheme></Button.Theme>")]
    public void Invalid_template_authority_types_names_and_modes_are_rejected_before_publication(string content)
    {
        var store = new UiSessionStore(); Assert.Throws<UiException>(() => store.Publish(new("invalid", 0, 1, $"<Button {Ns}>{content}</Button>"), "owner"));
        Assert.Empty(store.List("owner"));
    }
    [Fact]
    public void Unused_control_templates_and_cyclic_theme_bases_are_validated()
    {
        foreach (var resource in new[] {
            "<ControlTemplate x:Key=\"unused\" TargetType=\"Button\"><Border Width=\"{TemplateBinding Missing}\"/></ControlTemplate>",
            "<ControlTheme x:Key=\"a\" TargetType=\"Button\" BasedOn=\"{StaticResource b}\"/><ControlTheme x:Key=\"b\" TargetType=\"Button\" BasedOn=\"{StaticResource a}\"/>" })
            Assert.False(new UiCompiler().Compile($"<StackPanel {Ns}><StackPanel.Resources>{resource}</StackPanel.Resources></StackPanel>").Success);
    }
    [Fact]
    public void Template_instantiation_shares_surface_node_budget()
    {
        var source = $"<StackPanel {Ns}><Button>{Template}</Button><Button>{Template}</Button></StackPanel>";
        Assert.Throws<UiException>(() => new UiSessionStore(new UiCompiler(limits: new UiLimits(Nodes: 6))).Publish(new("limit", 0, 1, source), "owner"));
    }
}
