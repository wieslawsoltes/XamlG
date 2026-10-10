using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiBindingAuthoringTests
{
    private const string Ns = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private static UiPublish Source(string body, object? state = null, object? data = null) =>
        new("bindings", 0, 1, $"<StackPanel {Ns}>{body}</StackPanel>", J(state ?? new { }), J(data ?? new { }));
    private static UiElement Find(UiSnapshot snapshot, string key) => UiSessionStore.Flatten(snapshot.Roots).Single(node => node.Key == "/" + key);

    [Fact]
    public void DataContext_is_inherited_and_sibling_scopes_do_not_leak()
    {
        var snapshot = new UiSessionStore().Publish(Source("""
            <TextBlock ui:Key="global" Text="{Binding title}"/>
            <StackPanel DataContext="{Binding customer}">
              <TextBlock ui:Key="name" Text="{CompiledBinding name}"/>
              <StackPanel><StackPanel.DataContext><Binding Path="address"/></StackPanel.DataContext>
                <TextBlock ui:Key="city"><TextBlock.Text><Binding Path="city"/></TextBlock.Text></TextBlock>
              </StackPanel>
              <TextBlock ui:Key="again" Text="{Binding name}"/>
            </StackPanel>
            <TextBlock ui:Key="sibling" Text="{Binding title}"/>
            """, data: new { title = "Root", customer = new { name = "Ada", address = new { city = "Warsaw" } } }), "owner");
        Assert.Equal("Ada", Find(snapshot, "name").Properties["Text"].GetString());
        Assert.Equal("Warsaw", Find(snapshot, "city").Properties["Text"].GetString());
        Assert.Equal("Ada", Find(snapshot, "again").Properties["Text"].GetString());
        Assert.Equal("Root", Find(snapshot, "sibling").Properties["Text"].GetString());
    }
    [AvaloniaFact]
    public void Direct_two_way_state_bindings_use_native_input_adapters()
    {
        var store = new UiSessionStore();
        var first = store.Publish(Source("""
            <TextBox ui:Key="name" Text="{Binding state.name, Mode=TwoWay}"/>
            <Slider ui:Key="count" Value="{CompiledBinding state.count}" Minimum="0" Maximum="100"/>
            <TextBlock ui:Key="read" Text="{Binding state.name}"/>
            """, new { name = "Before", count = 3 }), "owner");
        using var renderer = new UiAvaloniaRenderer();
        renderer.StateChanged += change => renderer.Apply(store.ChangeState(change, "owner")); renderer.Apply(first);
        var input = Assert.IsType<TextBox>(renderer.Find("/name")); input.Text = "After";
        var current = store.Read(first.Id, "owner");
        Assert.Equal("After", current.State.GetProperty("name").GetString());
        Assert.Equal("After", Assert.IsType<TextBlock>(renderer.Find("/read")).Text);
        Assert.Same(input, renderer.Find("/name"));
        Assert.DoesNotContain("{Binding", UiSourceExporter.Xaml(current));
        Assert.IsType<StackPanel>(AvaloniaRuntimeXamlLoader.Load(UiSourceExporter.Xaml(current)));
    }
    [Fact]
    public void Paths_fallbacks_null_replacement_and_formats_are_typed_and_bounded()
    {
        var snapshot = new UiSessionStore().Publish(Source("""
            <TextBlock ui:Key="index" Text="{Binding rows[1].name}"/>
            <TextBlock ui:Key="key" Text="{Binding labels['display-name']}"/>
            <TextBlock ui:Key="missing" Text="{Binding missing.name, FallbackValue='Not found'}"/>
            <TextBlock ui:Key="null" Text="{Binding nullable, TargetNullValue='Empty'}"/>
            <TextBlock ui:Key="format" Text="{Binding amount, StringFormat='{}{0:F2} EUR'}"/>
            <ProgressBar ui:Key="numeric" Value="{Binding absent, FallbackValue=12}"/>
            """, data: new { rows = new[] { new { name = "A" }, new { name = "B" } }, labels = new Dictionary<string, string> { ["display-name"] = "Label" }, nullable = (string?)null, amount = 12.345m }), "owner");
        Assert.Equal("B", Find(snapshot, "index").Properties["Text"].GetString());
        Assert.Equal("Label", Find(snapshot, "key").Properties["Text"].GetString());
        Assert.Equal("Not found", Find(snapshot, "missing").Properties["Text"].GetString());
        Assert.Equal("Empty", Find(snapshot, "null").Properties["Text"].GetString());
        Assert.Equal("12.35 EUR", Find(snapshot, "format").Properties["Text"].GetString());
        Assert.Equal(12, Find(snapshot, "numeric").Properties["Value"].GetDecimal());
    }
    [AvaloniaFact]
    public void Content_templates_preserve_lexical_resources_context_and_keyed_controls()
    {
        var store = new UiSessionStore();
        var request = Source("""
            <StackPanel.Resources>
              <x:String x:Key="caption">Template</x:String>
              <DataTemplate x:Key="person"><StackPanel>
                <TextBlock Text="{StaticResource caption}"/>
                <Button x:Name="choose" Content="{Binding name}" ui:Action="choose"/>
              </StackPanel></DataTemplate>
            </StackPanel.Resources>
            <ContentControl ui:Key="a" Content="{Binding person}" ContentTemplate="{StaticResource person}"/>
            <ContentControl ui:Key="b" Content="{Binding person}"><ContentControl.ContentTemplate><StaticResource ResourceKey="person"/></ContentControl.ContentTemplate></ContentControl>
            """, new { selected = "" }, new { person = new { id = "a", name = "Alpha" } }) with
        { Actions = [new("choose", "state", Arguments: J(new { selected = "{ui:Expr item.id}" }))] };
        var first = store.Publish(request, "owner");
        using var renderer = new UiAvaloniaRenderer(); renderer.Apply(first);
        var buttons = UiSessionStore.Flatten(first.Roots).Where(node => node.Type == "Button").ToArray();
        Assert.Equal(2, buttons.Length); Assert.NotEqual(buttons[0].Key, buttons[1].Key);
        var button = renderer.Find(buttons[0].Key);
        var next = store.ChangeData(new(first.Id, first.Revision, J(new { person = new { id = "b", name = "Beta" } })), "owner");
        renderer.Apply(next); Assert.Same(button, renderer.Find(buttons[0].Key)); Assert.Equal("Beta", Assert.IsType<Button>(button).Content);
        var result = store.ApplyStateAction(new(next.Id, next.Revision, next.StateRevision, buttons[0].Key), "owner");
        Assert.Equal("b", result.State.GetProperty("selected").GetString());
        Assert.Contains("Template", result.FallbackMarkdown);
    }
    [Fact]
    public void Item_template_bindings_read_the_row_and_nested_context_not_global_data()
    {
        var snapshot = new UiSessionStore().Publish(Source("""
            <ItemsControl ItemsSource="{Binding rows}" ui:ItemKey="{Binding id}"><ItemsControl.ItemTemplate>
              <DataTemplate><StackPanel><TextBlock Text="{Binding name}"/>
                <StackPanel DataContext="{Binding child}"><TextBlock Text="{Binding name}"/></StackPanel>
              </StackPanel></DataTemplate>
            </ItemsControl.ItemTemplate></ItemsControl>
            """, data: new { name = "Wrong", rows = new[] { new { id = "row", name = "Right", child = new { name = "Child" } } } }), "owner");
        Assert.Equal(new[] { "Right", "Child" }, UiSessionStore.Flatten(snapshot.Roots).Where(node => node.Type == "TextBlock").Select(node => node.Properties["Text"].GetString()));
    }
    [Fact]
    public void Missing_path_update_keeps_the_committed_data_roots_and_revision()
    {
        var store = new UiSessionStore(); var first = store.Publish(Source("<TextBlock Text=\"{Binding title}\"/>", data: new { title = "Stable" }), "owner");
        Assert.Throws<UiException>(() => store.ChangeData(new(first.Id, first.Revision, J(new { other = "Bad" })), "owner"));
        Assert.Same(first, store.Read(first.Id, "owner"));
    }
    [Theory]
    [InlineData("<TextBox Text=\"{Binding data.name,Mode=TwoWay}\"/>")]
    [InlineData("<TextBox ui:Bind=\"name\" Text=\"{Binding state.name,Mode=TwoWay}\"/>")]
    [InlineData("<TextBox Text=\"{Binding state.nested.name,Mode=TwoWay}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding name,Converter=Untrusted}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding name,Mode=OneTime}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding name,Path=other}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding name.ToString()}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding value,StringFormat='{0,99999999}'}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding value,StringFormat='{0:F999999}'}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding value,StringFormat='{1}'}\"/>")]
    [InlineData("<StackPanel.Resources><DataTemplate x:Key=\"unused\"><Button Click=\"Execute\"/></DataTemplate></StackPanel.Resources>")]
    [InlineData("<StackPanel.Resources><DataTemplate x:Key=\"unused\"><TextBlock Text=\"{ui:Expr System.IO.File.ReadAllText(&quot;x&quot;)}\"/></DataTemplate></StackPanel.Resources>")]
    [InlineData("<StackPanel.Resources><DataTemplate x:Key=\"loop\"><ContentControl ContentTemplate=\"{StaticResource loop}\"/></DataTemplate></StackPanel.Resources>")]
    public void Invalid_bindings_and_unused_executable_templates_are_rejected(string body)
    {
        var store = new UiSessionStore(); Assert.Throws<UiException>(() => store.Publish(Source(body), "owner")); Assert.Empty(store.List("owner"));
    }
}
