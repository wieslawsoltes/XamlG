using System.Collections.Immutable;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Dispatcher-affine native renderer. Stable keys retain controls, caret/focus and subscriptions across value-only updates.</summary>
public sealed class UiAvaloniaRenderer : IDisposable
{
    private readonly UiAvaloniaCatalog _catalog;
    private readonly UiCatalog _schema;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly StackPanel _root = new();
    private UiSnapshot? _snapshot;
    private bool _applying, _disposed;
    public Control View => _root;
    public event Action<UiStateChange>? StateChanged;
    public event Action<UiActionCall>? ActionRequested;
    public UiAvaloniaRenderer(UiAvaloniaCatalog? catalog = null, UiCatalog? schema = null)
    { _catalog = catalog ?? UiAvaloniaCatalog.Default; _schema = schema ?? UiCatalog.Default; }
    public Control? Find(string key) => _entries.GetValueOrDefault(key)?.Control;
    public void Apply(UiSnapshot snapshot)
    {
        Dispatcher.UIThread.VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (_applying) throw new InvalidOperationException("Reentrant native UI update.");
        // Revalidate the inert boundary even when a caller did not obtain it from UiSessionStore.
        var nodes = UiSessionStore.Flatten(snapshot.Roots).ToArray();
        if (nodes.Length > 4096 || nodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            throw new UiException("invalid_tree", "Invalid or oversized native tree.");
        foreach (var node in nodes)
        {
            if (!_catalog.Registrations.TryGetValue(node.Type, out var native) || !_schema.Components.TryGetValue(node.Type, out var component))
                throw new UiException("unknown_component", "No trusted native factory: " + node.Type);
            if (node.Children.Length > component.MaximumChildren) throw new UiException("invalid_content", "Invalid native child count.");
            foreach (var property in node.Properties)
            {
                if (!component.Properties.TryGetValue(property.Key, out var definition) || !native.Setters.ContainsKey(property.Key)) throw new UiException("unknown_property", "No trusted property setter.");
                // Public descriptor validation is shared with the compiler.
                definition.ValidateValue(property.Value);
            }
        }
        _applying = true;
        try
        {
            var parents = new Dictionary<string, string>(StringComparer.Ordinal);
            Collect(snapshot.Roots, "", parents);
            var replaced = _entries.Values.Where(entry => !nodes.Any(node => node.Key == entry.Node.Key && node.Type == entry.Node.Type)).Select(entry => entry.Node.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in _entries.Values.ToArray())
            {
                var next = nodes.FirstOrDefault(node => node.Key == entry.Node.Key);
                if (next == null || next.Type != entry.Node.Type || parents[entry.Node.Key] != entry.Parent || replaced.Contains(entry.Parent)) Detach(entry.Control);
                if (next == null || next.Type != entry.Node.Type) { Unsubscribe(entry); _entries.Remove(entry.Node.Key); }
            }
            foreach (var node in nodes)
            {
                if (!_entries.TryGetValue(node.Key, out var entry))
                {
                    entry = new(node, _catalog.Registrations[node.Type].Create(), parents[node.Key]);
                    _entries.Add(node.Key, entry); Subscribe(entry);
                    Update(entry, node, true);
                }
                else { Update(entry, node, false); entry.Parent = parents[node.Key]; }
            }
            foreach (var node in nodes.Reverse()) Children(_entries[node.Key].Control, node.Children.Select(child => _entries[child.Key].Control).ToArray());
            Children(_root, snapshot.Roots.Select(node => _entries[node.Key].Control).ToArray());
            _snapshot = snapshot;
        }
        finally { _applying = false; }
    }
    private static void Collect(IEnumerable<UiElement> nodes, string parent, Dictionary<string, string> map)
    { foreach (var node in nodes) { map.Add(node.Key, parent); Collect(node.Children, node.Key, map); } }
    private void Update(Entry entry, UiElement node, bool initial)
    {
        var setters = _catalog.Registrations[node.Type].Setters;
        foreach (var old in entry.Node.Properties.Keys.Where(key => !node.Properties.ContainsKey(key)).ToArray()) setters[old](entry.Control, null);
        // Range endpoints must precede Value; input events caused by coercion remain suppressed.
        foreach (var property in node.Properties.OrderBy(p => p.Key == "Minimum" ? 0 : p.Key == "Maximum" ? 1 : p.Key == "Value" ? 3 : 2))
            if (initial || property.Key == _schema.Components[node.Type].InputProperty || !entry.Node.Properties.TryGetValue(property.Key, out var previous) || !JsonElement.DeepEquals(previous, property.Value) ||
                property.Key == "Value" && (Changed("Minimum") || Changed("Maximum")))
                setters[property.Key](entry.Control, property.Value);
        entry.Node = node;
        bool Changed(string key) => entry.Node.Properties.TryGetValue(key, out var old) != node.Properties.TryGetValue(key, out var next) || !JsonElement.DeepEquals(old, next);
    }
    private void Subscribe(Entry entry)
    {
        entry.PropertyChanged = (_, args) =>
        {
            if (_applying || _disposed || _snapshot == null || entry.Node.StateKey == null) return;
            object? value;
            if (entry.Control is TextBox text && args.Property == TextBox.TextProperty) value = text.Text ?? "";
            else if (entry.Control is Slider slider && args.Property == RangeBase.ValueProperty) value = slider.Value;
            else if (entry.Control is CheckBox toggle && args.Property == ToggleButton.IsCheckedProperty) value = toggle.IsChecked == true;
            else return;
            StateChanged?.Invoke(new(_snapshot.Id, _snapshot.Revision, _snapshot.StateRevision, entry.Node.StateKey, JsonSerializer.SerializeToElement(value)));
        };
        entry.Control.PropertyChanged += entry.PropertyChanged;
        if (entry.Control is Button button && entry.Control is not CheckBox)
        {
            entry.Click = (_, _) => { if (!_applying && !_disposed && _snapshot != null && entry.Node.ActionId != null) ActionRequested?.Invoke(new(_snapshot.Id, _snapshot.Revision, _snapshot.StateRevision, entry.Node.Key)); };
            button.Click += entry.Click;
        }
    }
    private static void Unsubscribe(Entry entry)
    { entry.Control.PropertyChanged -= entry.PropertyChanged; if (entry.Control is Button button && entry.Click != null) button.Click -= entry.Click; }
    private static void Children(Control parent, IReadOnlyList<Control> children)
    {
        switch (parent)
        {
            case Panel panel:
                for (var i = panel.Children.Count - 1; i >= 0; i--) if (!children.Contains(panel.Children[i])) panel.Children.RemoveAt(i);
                for (var i = 0; i < children.Count; i++)
                {
                    if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], children[i])) continue;
                    panel.Children.Remove(children[i]); panel.Children.Insert(i, children[i]);
                }
                break;
            case Decorator decorator: if (!ReferenceEquals(decorator.Child, children.FirstOrDefault())) decorator.Child = children.FirstOrDefault(); break;
            case ScrollViewer scroll: if (!ReferenceEquals(scroll.Content, children.FirstOrDefault())) scroll.Content = children.FirstOrDefault(); break;
            default: if (children.Count != 0) throw new UiException("invalid_content", "This native factory has no child container."); break;
        }
    }
    private static void Detach(Control control)
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        else if (control.Parent is Decorator decorator && ReferenceEquals(decorator.Child, control)) decorator.Child = null;
        else if (control.Parent is ContentControl content && ReferenceEquals(content.Content, control)) content.Content = null;
    }
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess(); if (_disposed) return; _disposed = true;
        foreach (var entry in _entries.Values) Unsubscribe(entry);
        _root.Children.Clear(); _entries.Clear(); _snapshot = null; StateChanged = null; ActionRequested = null;
    }
    private sealed class Entry(UiElement node, Control control, string parent)
    {
        internal UiElement Node = node;
        internal Control Control { get; } = control;
        internal string Parent = parent;
        internal EventHandler<AvaloniaPropertyChangedEventArgs>? PropertyChanged;
        internal EventHandler<RoutedEventArgs>? Click;
    }
}
