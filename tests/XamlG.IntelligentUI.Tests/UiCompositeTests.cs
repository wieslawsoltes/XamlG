using System.Text.Json;
using Avalonia.Headless.XUnit;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiCompositeTests
{
    private const string Namespaces = "xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"";
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static UiPublish Source(string content) => new("rich", 0, 1, "<StackPanel " + Namespaces + ">" + content + "</StackPanel>");

    [Fact]
    public void Composite_catalog_is_separate_from_native_control_contract()
    {
        Assert.Equal(42, UiCatalog.Default.Components.Count);
        Assert.Equal(14, UiCompositeCatalog.Components.Count);
        Assert.All(UiCompositeCatalog.Components.Values, component => Assert.StartsWith("ui:", component.Name));
    }

    [Fact]
    public void All_response_components_lower_to_known_native_operations()
    {
        var snapshot = new UiSessionStore().Publish(Source("""
            <ui:Heading Level="1">Title</ui:Heading><ui:Paragraph>Paragraph</ui:Paragraph>
            <ui:Badge>Ready</ui:Badge><ui:Card Title="Card"><ui:KeyValue Title="Key" Value="Value"/></ui:Card>
            <ui:Callout Title="Notice" Tone="Warning">Message</ui:Callout>
            <ui:Metric Title="Metric" Value="42" Detail="Details"/>
            <ui:CodeBlock Language="C#">Console.WriteLine(&quot;&lt;script&gt;&quot;);</ui:CodeBlock>
            <ui:Table Columns="A,B"><ui:TableRow><TextBlock Text="one"/><TextBlock Text="two"/></ui:TableRow></ui:Table>
            <ui:BarChart><ui:DataPoint Label="A" Value="-3"/><ui:DataPoint Label="B" Value="4"/></ui:BarChart>
            <ui:LineChart><ui:DataPoint Value="2" X="10"/><ui:DataPoint Value="5" X="20"/></ui:LineChart>
            <ui:ScatterChart><ui:DataPoint Value="3"/></ui:ScatterChart>
            """), "owner");
        var nodes = UiSessionStore.Flatten(snapshot.Roots).ToArray();
        Assert.All(nodes, node => Assert.True(UiAvaloniaCatalog.Default.Registrations.ContainsKey(node.Type), node.Type));
        Assert.Equal(nodes.Length, nodes.Select(node => node.Key).Distinct().Count());
        Assert.Contains("Console.WriteLine(\"<script>\");", snapshot.FallbackMarkdown);
        Assert.Contains("A: -3", snapshot.FallbackMarkdown);
        var xaml = UiSourceExporter.Xaml(snapshot);
        Assert.DoesNotContain("<ui:", xaml); Assert.DoesNotContain("ui:Expr", xaml);
        Assert.Contains("&lt;script&gt;", xaml);
    }

    [AvaloniaFact]
    public void Dashboard_has_reactive_native_table_chart_and_metric_and_local_reset()
    {
        var store = new UiSessionStore(); var initial = store.Publish(UiRichExamples.Dashboard(), "owner");
        using var view = new UiAvaloniaSession(store, UiPresentation.From(initial), "owner");
        Assert.Null(view.Diagnostic); Assert.Contains("Alpha: 10", view.Snapshot!.FallbackMarkdown);
        var keys = UiSessionStore.Flatten(initial.Roots).Select(node => node.Key).ToArray();
        var updated = store.ChangeState(new(initial.Id, initial.Revision, 0, "scale", J(2)), "owner");
        Assert.Contains("Alpha: 20", updated.FallbackMarkdown); Assert.Contains("100", updated.FallbackMarkdown);
        Assert.Equal(keys, UiSessionStore.Flatten(updated.Roots).Select(node => node.Key).ToArray());
        Assert.Null(view.Diagnostic);
        var reset = store.ApplyStateAction(new(updated.Id, updated.Revision, updated.StateRevision, "/reset-scale"), "owner");
        Assert.Equal(1, reset.State.GetProperty("scale").GetDecimal());
        Assert.Contains("Alpha: 10", reset.FallbackMarkdown);
    }

    [Theory]
    [InlineData("<ui:Table Columns=\"A,B\"><ui:TableRow><TextBlock/></ui:TableRow></ui:Table>")]
    [InlineData("<ui:Table Columns=\"A,,B\"/>")]
    [InlineData("<ui:Table Columns=\"A,B\" ColumnDefinitions=\"*\"/>")]
    [InlineData("<ui:TableRow/>")]
    [InlineData("<ui:DataPoint Value=\"1\"/>")]
    [InlineData("<ui:BarChart Minimum=\"0\" Maximum=\"1\"><ui:DataPoint Value=\"2\"/></ui:BarChart>")]
    [InlineData("<ui:LineChart><ui:DataPoint/></ui:LineChart>")]
    public void Invalid_compositions_are_rejected_without_publishing(string content)
    {
        var store = new UiSessionStore();
        Assert.Throws<UiException>(() => store.Publish(Source(content), "owner"));
        Assert.Empty(store.List("owner"));
    }

    [Theory]
    [InlineData("BarChart")][InlineData("LineChart")][InlineData("ScatterChart")]
    public void Empty_and_zero_series_have_finite_geometry(string type)
    {
        foreach (var points in new[] { "", "<ui:DataPoint Value=\"0\"/>" })
        {
            var snapshot = new UiSessionStore().Publish(Source($"<ui:{type}>{points}</ui:{type}>"), "owner");
            Assert.Contains("Range: 0", snapshot.FallbackMarkdown);
            Assert.All(UiSessionStore.Flatten(snapshot.Roots), node => UiTreeValidation.ValidateElement(node, UiCatalog.Default.Components[node.Type]));
        }
    }

    [Fact]
    public void Invalid_reactive_chart_domain_preserves_old_snapshot()
    {
        var store = new UiSessionStore();
        var request = Source("<Slider ui:Bind=\"n\" Minimum=\"0\" Maximum=\"20\"/><ui:BarChart Maximum=\"10\"><ui:DataPoint Value=\"{ui:Expr state.n}\"/></ui:BarChart>") with { InitialState = J(new { n = 2 }) };
        var initial = store.Publish(request, "owner");
        Assert.Equal("invalid_range", Assert.Throws<UiException>(() => store.ChangeState(new(initial.Id, initial.Revision, 0, "n", J(15)), "owner")).Code);
        Assert.Same(initial, store.Read(initial.Id, "owner"));
    }

    [Fact]
    public void Generated_nodes_and_depth_obey_the_original_resource_limits()
    {
        var nodes = new UiSessionStore(new UiCompiler(limits: new UiLimits(Nodes: 3)));
        Assert.Equal("node_limit", Assert.Throws<UiException>(() => nodes.Publish(Source("<ui:Metric Title=\"Title\" Value=\"1\" Detail=\"Detail\"/>"), "owner")).Code);
        var depth = new UiSessionStore(new UiCompiler(limits: new UiLimits(Depth: 2)));
        Assert.Equal("depth_limit", Assert.Throws<UiException>(() => depth.Publish(Source("<ui:Card Title=\"Title\"/>"), "owner")).Code);
    }
}
