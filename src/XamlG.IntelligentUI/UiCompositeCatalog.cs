using System.Collections.Immutable;
using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Trusted source-only components in urn:xamlg:intelligent-ui. Lowering produces
/// ordinary validated Avalonia operations; hosts never execute composite source.</summary>
public static class UiCompositeCatalog
{
    public static ImmutableDictionary<string, UiComponent> Components { get; } = CreateComponents();

    private static ImmutableDictionary<string, UiComponent> CreateComponents()
    {
        var common = UiCatalog.Default.Components["Separator"].Properties;
        var text = new UiProperty(UiPropertyKind.Text);
        var number = new UiProperty(UiPropertyKind.Number, -1000000, 1000000);
        var space = new UiProperty(UiPropertyKind.Integer, 0, 32);
        var result = ImmutableDictionary.CreateBuilder<string, UiComponent>(StringComparer.Ordinal);
        void Add(string name, int children = 0, string? content = null, string[]? childTypes = null,
            params (string Name, UiProperty Property)[] properties)
            => result.Add(name, new("ui:" + name, common.SetItems(properties.Select(p => new KeyValuePair<string, UiProperty>(p.Name, p.Property))),
                children, content, ChildTypes: childTypes?.Select(type => "ui:" + type).ToArray()));
        Add("Heading", content: "Text", properties: [("Text", text), ("Level", new(UiPropertyKind.Integer, 1, 6))]);
        Add("Paragraph", content: "Text", properties: [("Text", text)]);
        Add("Badge", content: "Text", properties: [("Text", text)]);
        Add("Card", 512, properties: [("Title", text), ("Space", space), ("Gap", space), ("Radius", new(UiPropertyKind.Integer, 0, 8))]);
        Add("Callout", content: "Text", properties: [("Title", text), ("Text", text), ("Tone", new(UiPropertyKind.Choice, Choices: ["Info", "Success", "Warning", "Error"]))]);
        Add("Metric", properties: [("Title", text), ("Value", text), ("Detail", text)]);
        Add("KeyValue", properties: [("Title", text), ("Value", text)]);
        Add("CodeBlock", content: "Text", properties: [("Text", text), ("Language", text), ("ViewportHeight", new(UiPropertyKind.Number, 80, 1000))]);
        Add("Table", 128, childTypes: ["TableRow"], properties: [("Columns", text), ("ColumnDefinitions", text), ("EmptyText", text)]);
        Add("TableRow", 16);
        foreach (var name in new[] { "BarChart", "LineChart", "ScatterChart" })
            Add(name, 128, childTypes: ["DataPoint"], properties:
            [("Title", text), ("Minimum", number), ("Maximum", number), ("PlotWidth", new(UiPropertyKind.Number, 120, 4000)),
             ("PlotHeight", new(UiPropertyKind.Number, 80, 2000)), ("Accent", new(UiPropertyKind.Color)), ("ShowLegend", new(UiPropertyKind.Boolean))]);
        Add("DataPoint", properties: [("Label", text), ("Value", number), ("X", number)]);
        return result.ToImmutable();
    }

    internal static ImmutableArray<UiElement> Lower(ImmutableArray<UiElement> roots, UiCatalog catalog, UiLimits limits)
        => new UiCompositeLowerer(catalog, limits).Run(roots);
}
