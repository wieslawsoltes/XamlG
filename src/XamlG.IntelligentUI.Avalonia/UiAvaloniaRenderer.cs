using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Dispatcher-affine keyed renderer. Typed input adapters, item/content containers and
/// control lifetimes use the same schema as compilation. No native property is set before validation.</summary>
public sealed class UiAvaloniaRenderer : IDisposable
{
    private readonly UiAvaloniaCatalog _catalog;
    private readonly UiCatalog _schema;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly RootPanel _root = new();
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
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_applying) throw new InvalidOperationException("Reentrant native UI update.");
        if (_snapshot is { } previous && previous.SessionId == snapshot.SessionId &&
            (snapshot.Revision < previous.Revision || snapshot.Revision == previous.Revision && snapshot.StateRevision < previous.StateRevision))
            throw new UiException("revision_conflict", "The native snapshot is older than the displayed state.");
        // Factories, detached setters and retirement callbacks are application code too.
        // Keep the guard active through preparation and rollback, not just publication.
        _applying = true;
        try { ApplyCore(snapshot, recover: true); }
        finally { _applying = false; }
    }
    private void ApplyCore(UiSnapshot snapshot, bool recover)
    {
        var nodes = new Dictionary<string, UiElement>(StringComparer.Ordinal);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<(UiElement Node, string Parent, int Depth)>();
        if (snapshot.Roots.IsDefault) throw new UiException("invalid_tree", "Uninitialized root collection.");
        foreach (var node in snapshot.Roots.Reverse()) pending.Push((node, "", 0));
        while (pending.TryPop(out var item))
        {
            var node = item.Node;
            if (node == null || string.IsNullOrEmpty(node.Key) || node.Key.Length > 8192 || item.Depth > 48 || nodes.Count >= 4096 || !nodes.TryAdd(node.Key, node)) throw new UiException("invalid_tree", "Invalid, duplicate or oversized native tree.");
            parents.Add(node.Key, item.Parent);
            if (!_catalog.Registrations.TryGetValue(node.Type, out var native) || !_schema.Components.TryGetValue(node.Type, out var component)) throw new UiException("unknown_component", "No trusted native factory: " + node.Type);
            UiTreeValidation.ValidateElement(node, component);
            if (node.Properties.Keys.Any(key => !native.Setters.ContainsKey(key))) throw new UiException("unknown_property", "No trusted property setter.");
            foreach (var child in node.Children.Reverse()) pending.Push((child, node.Key, item.Depth + 1));
        }
        var newSession = _snapshot != null && _snapshot.SessionId != snapshot.SessionId;
        var replacements = _entries.Values.Where(entry => newSession || !nodes.TryGetValue(entry.Node.Key, out var node) || node.Type != entry.Node.Type).Select(entry => entry.Node.Key).ToHashSet(StringComparer.Ordinal);
        var prepared = new Dictionary<string, Entry>(StringComparer.Ordinal);
        // Constructor/conversion failures for new controls occur while detached.
        try
        {
            foreach (var node in nodes.Values)
                if (!_entries.ContainsKey(node.Key) || replacements.Contains(node.Key))
                {
                    var registration = _catalog.Registrations[node.Type];
                    var entry = new Entry(node, registration.Create(), parents[node.Key], registration);
                    prepared.Add(node.Key, entry); Update(entry, node, true);
                }
        }
        catch { foreach (var entry in prepared.Values) Retire(entry); throw; }
        var previous = _snapshot;
        try
        {
            // Release every affected ownership edge before invoking retirement callbacks.
            // A retiring custom parent must not still own a child retained by the next tree.
            foreach (var entry in _entries.Values.Reverse())
                if (!nodes.ContainsKey(entry.Node.Key) || replacements.Contains(entry.Node.Key) || parents[entry.Node.Key] != entry.Parent || replacements.Contains(entry.Parent)) Detach(entry.Control);
            foreach (var key in replacements)
                if (_entries.Remove(key, out var retired)) Retire(retired);
            foreach (var node in nodes.Values)
            {
                if (prepared.Remove(node.Key, out var created)) { _entries.Add(node.Key, created); Subscribe(created); }
                else { var entry = _entries[node.Key]; Update(entry, node, false); entry.Parent = parents[node.Key]; }
            }
            foreach (var node in nodes.Values.Reverse()) Children(_entries[node.Key].Control, node.Children.Select(child => _entries[child.Key].Control).ToArray(), node.Properties.ContainsKey("ItemsSource"), node.Properties.ContainsKey("Content"));
            Children(_root, snapshot.Roots.Select(node => _entries[node.Key].Control).ToArray(), false, false);
            // Item insertion may coerce selection. Publish the validated selection only after all items exist.
            foreach (var node in nodes.Values)
                if (node.Properties.TryGetValue("SelectedIndex", out var selected)) _entries[node.Key].Registration.Setters["SelectedIndex"](_entries[node.Key].Control, selected);
            _snapshot = snapshot;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            foreach (var entry in prepared.Values) Retire(entry);
            if (recover && previous != null)
            {
                try { Reset(); ApplyCore(previous, recover: false); }
                catch (Exception rollback) when (rollback is not OutOfMemoryException) { Reset(); throw new AggregateException("Native update and recovery failed.", error, rollback); }
            }
            else Reset();
            throw;
        }
    }
    private void Update(Entry entry, UiElement node, bool initial)
    {
        var setters = entry.Registration.Setters;
        foreach (var old in entry.Node.Properties.Keys.Where(key => !node.Properties.ContainsKey(key)).ToArray()) setters[old](entry.Control, null);
        foreach (var property in node.Properties.OrderBy(p => p.Key == "Minimum" ? 0 : p.Key == "Maximum" ? 1 : p.Key == "Value" ? 3 : 2))
        {
            if (property.Key == "SelectedIndex") continue;
            if (initial || property.Key == _schema.Components[node.Type].InputProperty || !entry.Node.Properties.TryGetValue(property.Key, out var previous) || !JsonElement.DeepEquals(previous, property.Value) || property.Key == "Value" && (Changed("Minimum") || Changed("Maximum"))) setters[property.Key](entry.Control, property.Value);
        }
        entry.Node = node;
        bool Changed(string key) => entry.Node.Properties.TryGetValue(key, out var old) != node.Properties.TryGetValue(key, out var next) || !JsonElement.DeepEquals(old, next);
    }
    private void Subscribe(Entry entry)
    {
        entry.PropertyChanged = (_, args) =>
        {
            if (_applying || _disposed || _snapshot == null || entry.Node.StateKey == null) return;
            JsonElement? value = entry.Registration.ReadInput?.Invoke(entry.Control, args);
            if (value == null && entry.Registration.ReadInput == null)
            {
                if (entry.Control is TextBox text && args.Property == TextBox.TextProperty) value = JsonSerializer.SerializeToElement(text.Text ?? "");
                else if (entry.Control is Slider slider && args.Property == RangeBase.ValueProperty) value = JsonSerializer.SerializeToElement(slider.Value);
                else if (entry.Control is CheckBox toggle && args.Property == ToggleButton.IsCheckedProperty) value = JsonSerializer.SerializeToElement(toggle.IsChecked == true);
            }
            if (value is { } changed) StateChanged?.Invoke(new(_snapshot.Id, _snapshot.Revision, _snapshot.StateRevision, entry.Node.StateKey, changed));
        };
        entry.Control.PropertyChanged += entry.PropertyChanged;
        if (entry.Control is Button button && _schema.Components[entry.Node.Type].SupportsAction)
        {
            entry.Click = (_, _) => { if (!_applying && !_disposed && _snapshot != null && entry.Node.ActionId != null) ActionRequested?.Invoke(new(_snapshot.Id, _snapshot.Revision, _snapshot.StateRevision, entry.Node.Key)); };
            button.Click += entry.Click;
        }
    }
    private static void Retire(Entry entry)
    {
        entry.Control.PropertyChanged -= entry.PropertyChanged;
        if (entry.Control is Button button && entry.Click != null) button.Click -= entry.Click;
        entry.Registration.Retire?.Invoke(entry.Control);
    }
    private static void Children(Control parent, IReadOnlyList<Control> children, bool itemsSource, bool scalarContent)
    {
        switch (parent)
        {
            case Panel panel:
            {
                // Membership checks are linear in sibling count overall rather than n*m.
                // Native collection moves still carry the collection's own ordering cost.
                var retained = new HashSet<Control>(children, ReferenceEqualityComparer.Instance);
                for (var i = panel.Children.Count - 1; i >= 0; i--) if (!retained.Contains(panel.Children[i])) panel.Children.RemoveAt(i);
                for (var i = 0; i < children.Count; i++)
                {
                    if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], children[i])) continue;
                    panel.Children.Remove(children[i]); panel.Children.Insert(i, children[i]);
                }
                break;
            }
            // Viewbox is a Control with its own logical child, not a Decorator.
            case Viewbox viewbox: if (!ReferenceEquals(viewbox.Child, children.FirstOrDefault())) viewbox.Child = children.FirstOrDefault(); break;
            case Decorator decorator: if (!ReferenceEquals(decorator.Child, children.FirstOrDefault())) decorator.Child = children.FirstOrDefault(); break;
            case ItemsControl items when !itemsSource:
            {
                var retained = new HashSet<Control>(children, ReferenceEqualityComparer.Instance);
                for (var i = items.Items.Count - 1; i >= 0; i--) if (items.Items[i] is not Control control || !retained.Contains(control)) items.Items.RemoveAt(i);
                for (var i = 0; i < children.Count; i++)
                {
                    if (i < items.Items.Count && ReferenceEquals(items.Items[i], children[i])) continue;
                    items.Items.Remove(children[i]); items.Items.Insert(i, children[i]);
                }
                break;
            }
            case ItemsControl: break;
            case ContentControl content when !scalarContent: if (!ReferenceEquals(content.Content, children.FirstOrDefault())) content.Content = children.FirstOrDefault(); break;
            case ContentControl: break;
            default: if (children.Count != 0) throw new UiException("invalid_content", "This native factory has no child container."); break;
        }
    }
    private static void Detach(Control control)
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        else if (control.Parent is Viewbox viewbox && ReferenceEquals(viewbox.Child, control)) viewbox.Child = null;
        else if (control.Parent is Decorator decorator && ReferenceEquals(decorator.Child, control)) decorator.Child = null;
        else if (control.Parent is ContentControl content && ReferenceEquals(content.Content, control)) content.Content = null;
        else if (control.Parent is ItemsControl items && items.ItemsSource == null) items.Items.Remove(control);
    }
    private void Reset()
    {
        foreach (var entry in _entries.Values.Reverse()) Detach(entry.Control);
        foreach (var entry in _entries.Values) Retire(entry);
        _root.Children.Clear(); _entries.Clear(); _snapshot = null;
    }
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess(); if (_disposed) return;
        if (_applying) throw new InvalidOperationException("Cannot dispose during a native UI update.");
        _disposed = true; Reset(); StateChanged = null; ActionRequested = null;
    }
    /// <summary>A single root is an actual viewport, not an implicit vertical StackPanel.
    /// Multiple response roots retain the existing vertical-flow behavior.</summary>
    private sealed class RootPanel : StackPanel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            if (Children.Count != 1) return base.MeasureOverride(availableSize);
            Children[0].Measure(availableSize);
            return Children[0].DesiredSize;
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Children.Count != 1) return base.ArrangeOverride(finalSize);
            Children[0].Arrange(new Rect(finalSize));
            return finalSize;
        }
    }
    private sealed class Entry(UiElement node, Control control, string parent, UiControlRegistration registration)
    {
        internal UiElement Node = node;
        internal Control Control { get; } = control;
        internal string Parent = parent;
        internal UiControlRegistration Registration { get; } = registration;
        internal EventHandler<AvaloniaPropertyChangedEventArgs>? PropertyChanged;
        internal EventHandler<RoutedEventArgs>? Click;
    }
}
