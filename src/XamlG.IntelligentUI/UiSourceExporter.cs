using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

public static class UiSourceExporter
{
    /// <summary>Resolved static Avalonia XAML. Dynamic state/actions remain in the intelligent source and C# export.</summary>
    public static string Xaml(UiSnapshot snapshot)
    {
        XNamespace ns = UiCatalog.AvaloniaNamespace;
        XElement Convert(UiElement node)
        {
            var element = new XElement(ns + node.Type);
            foreach (var property in node.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()!,
                    JsonValueKind.True => "True", JsonValueKind.False => "False",
                    _ => property.Value.GetRawText()
                };
                if (property.Value.ValueKind == JsonValueKind.String && value.StartsWith('{')) value = "{}" + value;
                element.Add(new XAttribute(property.Key, value));
            }
            foreach (var child in node.Children) element.Add(Convert(child));
            return element;
        }
        return snapshot.Roots.Length == 1 ? Convert(snapshot.Roots[0]).ToString() : new XElement(ns + "StackPanel", snapshot.Roots.Select(Convert)).ToString();
    }

    /// <summary>Fixed trusted C# wrapper around an inert JSON declaration. Recreates the reactive session,
    /// not just a static screenshot. The embedding application explicitly handles action intents.</summary>
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
            private readonly UiSessionStore _store = new();
            private readonly UiAvaloniaSession _session;
            public Control View => _session.View;
            public event Action<UiActionIntent>? ActionRequested;
            public GeneratedIntelligentView()
            {
                var request = JsonSerializer.Deserialize<UiPublish>(
        """ + "\n            " + literal + ", AutomationJson.Options)!;\n" + """
                var snapshot = _store.Publish(request, "application");
                _session = new UiAvaloniaSession(_store, UiPresentation.From(snapshot), "application");
                _session.ActionRequested += call => ActionRequested?.Invoke(_store.PrepareAction(call, "application"));
            }
            public void Dispose()
            {
                _session.Dispose();
                _store.Clear();
                ActionRequested = null;
            }
        }
        """;
    }
}
