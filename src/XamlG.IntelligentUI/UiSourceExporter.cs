using System.Text.Json;
using System.Xml.Linq;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

public static class UiSourceExporter
{
    /// <summary>Resolved static Avalonia XAML. Arrays become real item values, not JSON strings;
    /// nullable values use x:Null. Dynamic state and actions remain in the reactive C# export.</summary>
    public static string Xaml(UiSnapshot snapshot)
    {
        XNamespace ns = UiCatalog.AvaloniaNamespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Convert(UiElement node)
        {
            var element = new XElement(ns + node.Type);
            var items = new List<XElement>();
            foreach (var property in node.Properties.OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                if (property.Key == "ItemsSource" && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray()) items.Add(new XElement(x + "String", item.GetString()));
                    continue;
                }
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()!,
                    JsonValueKind.True => "True", JsonValueKind.False => "False",
                    JsonValueKind.Null => "{x:Null}",
                    _ => property.Value.GetRawText()
                };
                if (property.Value.ValueKind == JsonValueKind.String && value.StartsWith('{')) value = "{}" + value;
                element.Add(new XAttribute(property.Key, value));
            }
            foreach (var item in items) element.Add(item);
            foreach (var child in node.Children) element.Add(Convert(child));
            return element;
        }
        var root = snapshot.Roots.Length == 1 ? Convert(snapshot.Roots[0]) : new XElement(ns + "StackPanel", snapshot.Roots.Select(Convert));
        root.Add(new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName));
        return root.ToString();
    }
    /// <summary>Trusted wrapper around an inert JSON declaration. Optional host-supplied compiler
    /// and native catalogs preserve application extensions; full C# still requires an approved compiler.</summary>
    public static string CSharp(UiSnapshot snapshot)
    {
        var request = new UiPublish(snapshot.Id, 0, 1, snapshot.Xaml, snapshot.State, snapshot.Data,
            snapshot.Actions.ToArray(), null, snapshot.IsFinal);
        var literal = JsonSerializer.Serialize(JsonSerializer.Serialize(request, AutomationJson.Options));
        return """
        // Reference XamlG.IntelligentUI.Avalonia. Construct and dispose on the Avalonia UI thread.
        using System;
        using System.Text.Json;
        using Avalonia.Controls;
        using XamlG.Automation;
        using XamlG.IntelligentUI;
        using XamlG.IntelligentUI.Avalonia;

        public sealed class GeneratedIntelligentView : IDisposable
        {
            private readonly UiSessionStore _store;
            private readonly UiAvaloniaSession _session;
            public Control View => _session.View;
            public UiSnapshot Snapshot => _session.Snapshot!;
            public event Action<UiActionIntent>? ActionRequested;
            public GeneratedIntelligentView() : this(null, null) { }
            public GeneratedIntelligentView(UiCompiler? compiler, UiAvaloniaCatalog? nativeCatalog = null)
            {
                _store = new UiSessionStore(compiler);
                var request = JsonSerializer.Deserialize<UiPublish>(
        """ + "\n            " + literal + ", AutomationJson.Options)!;\n" + """
                var snapshot = _store.Publish(request, "application");
                _session = new UiAvaloniaSession(_store, UiPresentation.From(snapshot), "application", nativeCatalog);
                _session.ActionRequested += call => ActionRequested?.Invoke(_store.PrepareAction(call, "application"));
            }
            public void UpdateData(JsonElement data)
            {
                var snapshot = Snapshot;
                _store.ChangeData(new UiDataChange(snapshot.Id, snapshot.Revision, data), "application");
            }
            public void Dispose()
            {
                _session.Dispose(); _store.Clear(); ActionRequested = null;
            }
        }
        """;
    }
}
