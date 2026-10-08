using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlG.Runtime;

namespace XamlG.AvaloniaRuntime.Inspection;

/// <summary>
/// Dispatcher-affine inspection of an actual preview. Handles follow object identity, never
/// child indices. Removed tree objects and handles from a replaced preview cannot be mutated.
/// Runtime mutations are deliberately separate from source/designer transactions.
/// </summary>
public sealed partial class AvaloniaRuntimeInspector : IDisposable
{
    private readonly AvaloniaObject _root;
    private readonly TimeProvider _timeProvider;
    private readonly ConditionalWeakTable<AvaloniaObject, ObjectIdentity> _identities = new();
    private readonly Dictionary<string, AvaloniaObject> _objects = new(StringComparer.Ordinal);
    private readonly Queue<RuntimeChange> _changes = new();
    private readonly Dictionary<(string ObjectId, string Event), Action> _eventCleanup = new();
    private long _nextId, _sequence;
    private string _topology = "";
    private bool _disposed;

    public AvaloniaRuntimeInspector(AvaloniaObject root, int maximumNodes = 10000, int maximumDepth = 128)
        : this(root, TimeProvider.System, maximumNodes, maximumDepth) { }

    /// <summary>Creates an inspector with the host's clock for retained-object lease expiry.</summary>
    public AvaloniaRuntimeInspector(AvaloniaObject root, TimeProvider timeProvider, int maximumNodes = 10000, int maximumDepth = 128)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(timeProvider);
        Dispatcher.UIThread.VerifyAccess();
        if (maximumNodes is < 1 or > 100000 || maximumDepth is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        _root = root; _timeProvider = timeProvider; MaximumNodes = maximumNodes; MaximumDepth = maximumDepth;
    }

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public long Revision { get; private set; }
    public event Action<RuntimeChange>? RuntimeChanged;
    public int MaximumNodes { get; }
    public int MaximumDepth { get; }

    public RuntimeSnapshot Capture()
    {
        VerifyAccess();
        var seen = new HashSet<AvaloniaObject>(ReferenceEqualityComparer.Instance);
        var ordered = new List<AvaloniaObject>();
        // Browser popups live on an overlay beside the preview root. Include only
        // hosts whose placement target is reached from this inspected project.
        var popupHosts = (_root is Control rootControl ? TopLevel.GetTopLevel(rootControl)?.GetVisualDescendants() : null)?
            .OfType<Avalonia.Controls.Primitives.OverlayPopupHost>().Take(MaximumNodes)
            .Select(host => (Host: host, Parent: PopupOwner(host))).Where(item => item.Parent?.PlacementTarget != null)
            .ToLookup(item => item.Parent!.PlacementTarget, item => item.Host);
        var pending = new Queue<(AvaloniaObject Object, int Depth)>();
        pending.Enqueue((_root, 0));
        while (pending.TryDequeue(out var entry))
        {
            if (!seen.Add(entry.Object)) continue;
            if (seen.Count > MaximumNodes || entry.Depth > MaximumDepth)
                throw new InvalidOperationException("The runtime tree exceeds the configured inspection limits.");
            ordered.Add(entry.Object);
            foreach (var child in VisualChildren(entry.Object).Concat(LogicalChildren(entry.Object)))
                pending.Enqueue((child, entry.Depth + 1));
            if (entry.Object is Control control)
            {
                if (control.ContextMenu is { IsOpen: true } menu) pending.Enqueue((menu, entry.Depth + 1));
                if (popupHosts != null) foreach (var popup in popupHosts[control]) pending.Enqueue((popup, entry.Depth + 1));
            }
        }

        // Publish only after traversal succeeds; a failed bounded read does not retire handles.
        foreach (var old in _objects.Where(pair => !seen.Contains(pair.Value)).ToArray())
        {
            old.Value.PropertyChanged -= OnPropertyChanged;
            foreach (var watch in _eventCleanup.Keys.Where(key => key.ObjectId == old.Key).ToArray())
            { _eventCleanup[watch](); _eventCleanup.Remove(watch); }
            RetireBindings(old.Key);
            RetireObjectWatches(old.Key);
            RetireObjectHandles(old.Key);
            _objects.Remove(old.Key);
        }
        foreach (var obj in ordered)
        {
            if (_objects.TryAdd(Id(obj), obj)) obj.PropertyChanged += OnPropertyChanged;
        }
        var topology = string.Join(";", ordered.Select(obj => Id(obj) + ":v=" +
            string.Join(',', VisualChildren(obj).Select(Id)) + ":l=" + string.Join(',', LogicalChildren(obj).Select(Id))));
        if (_topology != topology)
        {
            _topology = topology;
            Changed(Id(_root), "tree", "topology", null);
        }
        var nodes = ordered.Select(DescribeNode).ToArray();
        return new(SessionId, Revision, Id(_root), nodes);
    }

    public IReadOnlyList<RuntimeProperty> Properties(string objectId, bool includeClr = true)
    {
        var obj = Resolve(objectId);
        var properties = Registered(obj).OrderBy(Key, StringComparer.Ordinal).ToArray();
        var result = new List<RuntimeProperty>();
        foreach (var property in properties)
        {
            try
            {
                var diagnostic = obj.GetDiagnostic(property);
                result.Add(new(Key(property), property.Name, TypeName(property.OwnerType), TypeName(property.PropertyType),
                    property.IsAttached ? "attached" : property.IsDirect ? "direct" : "styled", property.IsReadOnly,
                    DescribeObjectValue(diagnostic.Value, objectId), obj.IsSet(property), obj.IsAnimating(property), diagnostic.Priority.ToString()));
            }
            catch (Exception error)
            {
                result.Add(new(Key(property), property.Name, TypeName(property.OwnerType), TypeName(property.PropertyType),
                    "registered", property.IsReadOnly, null, Error: ErrorText(error)));
            }
        }
        if (includeClr)
        {
            var names = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var property in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0 && !names.Contains(p.Name))
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                RuntimeValue? value = null; string? error = null;
                try { value = DescribeObjectValue(property.GetValue(obj), objectId); }
                catch (Exception exception) { error = ErrorText(exception); }
                result.Add(new("clr:" + property.Name, property.Name, TypeName(property.DeclaringType!),
                    TypeName(property.PropertyType), "clr", property.SetMethod?.IsPublic != true, value, Error: error));
            }
        }
        return result;
    }

    public RuntimeProperty SetProperty(string objectId, string propertyKey, JsonElement value, long expectedRevision)
    {
        var obj = ResolveForMutation(objectId, expectedRevision);
        if (propertyKey.StartsWith("clr:", StringComparison.Ordinal))
        {
            var property = obj.GetType().GetProperty(propertyKey[4..], BindingFlags.Public | BindingFlags.Instance);
            if (property?.SetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0)
                throw new InvalidOperationException("The CLR property has no public non-indexed setter.");
            var converted = ConvertValue(value, property.PropertyType);
            property.SetValue(obj, converted);
            Changed(objectId, "property", propertyKey, DescribeValue(converted));
        }
        else
        {
            var property = FindProperty(obj, propertyKey);
            if (property.IsReadOnly) throw new InvalidOperationException("The property is read-only.");
            var converted = ConvertValue(value, property.PropertyType);
            RetireBinding(objectId, Key(property));
            obj.SetValue(property, converted);
        }
        return Properties(objectId).Single(p => p.Key == propertyKey ||
            (!propertyKey.Contains('.') && !propertyKey.Contains(':') && p.Name == propertyKey));
    }

    public void ClearProperty(string objectId, string propertyKey, long expectedRevision)
    {
        var obj = ResolveForMutation(objectId, expectedRevision);
        var property = FindProperty(obj, propertyKey);
        if (property.IsReadOnly) throw new InvalidOperationException("The property is read-only.");
        RetireBinding(objectId, Key(property));
        obj.ClearValue(property);
    }

    public void SetClasses(string objectId, IReadOnlyList<string> classes, long expectedRevision)
    {
        if (classes.Count > 256 || classes.Any(c => string.IsNullOrWhiteSpace(c) || c.Any(char.IsWhiteSpace) || c.StartsWith(':')))
            throw new ArgumentException("Supply at most 256 non-empty class names without whitespace or pseudo classes.", nameof(classes));
        var obj = ResolveForMutation(objectId, expectedRevision) as StyledElement
            ?? throw new InvalidOperationException("The runtime object is not a styled element.");
        var candidate = classes.Distinct(StringComparer.Ordinal).ToArray();
        // Preserve framework-owned pseudo classes.
        foreach (var name in obj.Classes.Where(c => !c.StartsWith(':')).ToArray()) obj.Classes.Remove(name);
        obj.Classes.AddRange(candidate);
        Changed(objectId, "classes", "Classes", DescribeValue(string.Join(' ', candidate)));
    }

    public IReadOnlyDictionary<string, RuntimeValue> Resources(string objectId)
    {
        var obj = Resolve(objectId) as StyledElement ?? throw new InvalidOperationException("The object has no resources.");
        if (obj.Resources.Count > MaximumNodes) throw new InvalidOperationException("Too many resource entries.");
        return obj.Resources.ToDictionary(p => Convert.ToString(p.Key, CultureInfo.InvariantCulture) ?? "", p => DescribeObjectValue(p.Value, objectId));
    }

    public RuntimeValue FindResource(string objectId, string key)
    {
        var obj = Resolve(objectId) as StyledElement ?? throw new InvalidOperationException("The object has no resources.");
        return obj.TryFindResource(key, out var value) ? DescribeObjectValue(value, objectId) : throw new KeyNotFoundException("Resource not found: " + key);
    }

    public void SetResource(string objectId, string key, JsonElement value, string? typeName, bool remove, long expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A resource key is required.", nameof(key));
        var obj = ResolveForMutation(objectId, expectedRevision) as StyledElement
            ?? throw new InvalidOperationException("The object has no resources.");
        if (remove) obj.Resources.Remove(key);
        else obj.Resources[key] = ConvertValue(value, ResourceType(typeName));
        Changed(objectId, "resource", key, remove ? null : DescribeValue(obj.Resources[key]));
    }

    public IReadOnlyList<string> Events(string objectId)
    {
        var obj = Resolve(objectId);
        return RegisteredEvents(obj.GetType()).Select(e => TypeName(e.OwnerType) + "." + e.Name).ToArray();
    }

    public void RaiseEvent(string objectId, string eventName, long expectedRevision)
    {
        var obj = ResolveForMutation(objectId, expectedRevision) as Interactive
            ?? throw new InvalidOperationException("The object is not interactive.");
        var routedEvent = FindEvent(obj, eventName);
        if (routedEvent.EventArgsType != typeof(RoutedEventArgs))
            throw new InvalidOperationException("This event requires typed input arguments. Use a supported input operation.");
        obj.RaiseEvent(new RoutedEventArgs(routedEvent));
        Changed(objectId, "event", eventName, null);
    }

    public void WatchEvent(string objectId, string eventName)
    {
        var obj = Resolve(objectId) as Interactive ?? throw new InvalidOperationException("The object is not interactive.");
        var routedEvent = FindEvent(obj, eventName);
        var key = (objectId, TypeName(routedEvent.OwnerType) + "." + routedEvent.Name);
        if (_eventCleanup.ContainsKey(key)) return;
        if (_eventCleanup.Count >= 128) throw new InvalidOperationException("At most 128 event subscriptions are supported.");
        EventHandler<RoutedEventArgs> handler = (_, args) => Changed(objectId, "event", eventName, DescribeValue(args.Handled));
        obj.AddHandler(routedEvent, handler, RoutingStrategies.Direct | RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
        _eventCleanup.Add(key, () => obj.RemoveHandler(routedEvent, handler));
    }

    public void ClearEventWatches()
    {
        VerifyAccess();
        foreach (var cleanup in _eventCleanup.Values) cleanup();
        _eventCleanup.Clear();
    }

    public bool Focus(string objectId, long expectedRevision)
    {
        var obj = ResolveForMutation(objectId, expectedRevision) as Avalonia.Input.InputElement
            ?? throw new InvalidOperationException("The object cannot receive focus.");
        return obj.Focus();
    }

    public void UpdateLayout(string objectId, long expectedRevision)
    {
        var obj = ResolveForMutation(objectId, expectedRevision) as Control
            ?? throw new InvalidOperationException("The object is not a control.");
        obj.InvalidateMeasure(); obj.InvalidateArrange(); obj.UpdateLayout();
    }

    public void BringIntoView(string objectId, long expectedRevision)
    {
        var obj = ResolveForMutation(objectId, expectedRevision) as Control
            ?? throw new InvalidOperationException("The object is not a control.");
        obj.BringIntoView();
    }

    public string? HitTest(double x, double y)
    {
        Capture();
        return _root is Visual root && root.GetVisualAt(new Point(x, y)) is { } found ? Id(found) : null;
    }

    public RuntimeChanges Changes(long afterSequence = 0)
    {
        VerifyAccess();
        if (afterSequence < 0 || afterSequence > _sequence) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        Capture();
        return new(_sequence, _changes.TryPeek(out var first) && afterSequence < first.Sequence - 1,
            _changes.Where(e => e.Sequence > afterSequence).ToArray());
    }

    public AvaloniaObject Resolve(string objectId)
    {
        Capture();
        return _objects.TryGetValue(objectId, out var obj) ? obj : throw new KeyNotFoundException("The runtime handle is stale or unknown.");
    }

    private AvaloniaObject ResolveForMutation(string objectId, long expectedRevision)
    {
        var obj = Resolve(objectId);
        if (Revision != expectedRevision) throw new InvalidOperationException($"Runtime revision conflict: expected {expectedRevision}, actual {Revision}. Inspect again before editing.");
        return obj;
    }

    private RuntimeNode DescribeNode(AvaloniaObject obj)
    {
        var styled = obj as StyledElement;
        RuntimeBounds? bounds = null; XamlSourceInfo? source = null; var sourceOwned = false;
        if (obj is Visual visual)
        {
            var rect = visual.Bounds;
            var point = _root is Visual root ? visual.TranslatePoint(default, root) ?? default : default;
            bounds = new(rect.X, rect.Y, rect.Width, rect.Height, point.X, point.Y, visual.IsVisible);
            var node = AvaloniaVisualInspector.FindSource(_root as Visual ?? visual, visual);
            source = node?.Source; sourceOwned = node != null && ReferenceEquals(node.Instance, obj);
        }
        var visualParent = (obj as Visual)?.GetVisualParent();
        var logicalParent = (obj as ILogical)?.LogicalParent as AvaloniaObject;
        return new(Id(obj), TypeName(obj.GetType()), styled?.Name,
            visualParent != null && _objects.ContainsKey(Id(visualParent)) ? Id(visualParent) : null,
            logicalParent != null && _objects.ContainsKey(Id(logicalParent)) ? Id(logicalParent) : null,
            VisualChildren(obj).Select(Id).ToArray(), LogicalChildren(obj).Select(Id).ToArray(),
            styled?.Classes.ToArray() ?? [], styled == null ? null : DescribeValue(styled.DataContext), bounds, source, sourceOwned);
    }

    public RuntimeValue DescribeValue(object? value)
    {
        VerifyAccess();
        if (value == null) return new("null", null);
        var type = TypeName(value.GetType());
        if (value is AvaloniaObject obj)
            return _identities.TryGetValue(obj, out var identity) && _objects.ContainsKey(identity.Id)
                ? new(type, null, identity.Id, ReferenceKind: "tree") : new(type, null);
        if (value is string text) return new(type, text.Length <= 16384 ? text : text[..16384], Truncated: text.Length > 16384);
        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal) return new(type, value);
        if (value is double d && double.IsFinite(d)) return new(type, d);
        if (value is float f && float.IsFinite(f)) return new(type, f);
        // Never traverse arbitrary application objects or serialize object graphs.
        if (value.GetType().IsEnum || value.GetType().IsValueType || value is Avalonia.Media.IBrush)
        {
            var display = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            return new(type, display.Length <= 16384 ? display : display[..16384], Truncated: display.Length > 16384);
        }
        return new(type, null);
    }

    public static object? ConvertValue(JsonElement value, Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                throw new ArgumentException("Null is not valid for " + targetType.Name);
            return null;
        }
        if (type == typeof(object)) return Untyped(value);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (type == typeof(string)) return text;
            if (type.IsEnum) return Enum.Parse(type, text, ignoreCase: false);
            if (type == typeof(Avalonia.Media.IBrush)) return Avalonia.Media.Brush.Parse(text);
            var converter = TypeDescriptor.GetConverter(type);
            if (converter.CanConvertFrom(typeof(string))) return converter.ConvertFromInvariantString(text);
            var parse = type.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public, [typeof(string)]);
            if (parse != null && type.IsAssignableFrom(parse.ReturnType)) return parse.Invoke(null, [text]);
        }
        if (type.IsPrimitive || type == typeof(decimal) || type == typeof(string))
            return JsonSerializer.Deserialize(value.GetRawText(), targetType);
        throw new ArgumentException("Use a literal supported by the property's invariant type converter: " + TypeName(type));
    }

    private static object? Untyped(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var number) ? number : value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(Untyped).ToArray(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => Untyped(p.Value)),
        _ => throw new ArgumentException("A JSON value is required.")
    };

    private static Type ResourceType(string? name) => name switch
    {
        null or "object" => typeof(object), "string" => typeof(string), "double" => typeof(double),
        "bool" => typeof(bool), "int" => typeof(int), "Color" => typeof(Avalonia.Media.Color),
        "Brush" => typeof(Avalonia.Media.IBrush), "Thickness" => typeof(Thickness),
        "CornerRadius" => typeof(CornerRadius), _ => throw new ArgumentException("Unsupported resource literal type.")
    };

    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(Avalonia.Controls.Primitives.OverlayPopupHost))]
    private static Avalonia.Controls.Primitives.Popup? PopupOwner(Avalonia.Controls.Primitives.OverlayPopupHost host) =>
        typeof(Avalonia.Controls.Primitives.OverlayPopupHost).GetProperty("InteractiveParent", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(host) as Avalonia.Controls.Primitives.Popup;

    private static IEnumerable<AvaloniaObject> VisualChildren(AvaloniaObject obj) =>
        obj is Visual visual ? visual.GetVisualChildren() : [];
    private static IEnumerable<AvaloniaObject> LogicalChildren(AvaloniaObject obj) =>
        obj is ILogical logical ? logical.LogicalChildren.OfType<AvaloniaObject>() : [];
    private static IEnumerable<AvaloniaProperty> Registered(AvaloniaObject obj) =>
        AvaloniaPropertyRegistry.Instance.GetRegistered(obj.GetType())
            .Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(obj.GetType())).Distinct();
    private static AvaloniaProperty FindProperty(AvaloniaObject obj, string key)
    {
        var matches = Registered(obj).Where(p => Key(p) == key || p.Name == key).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException("Unknown or ambiguous property. Use the inspected property key.");
    }
    private static IEnumerable<RoutedEvent> RegisteredEvents(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
            foreach (var item in RoutedEventRegistry.Instance.GetRegistered(current)) yield return item;
    }
    private static RoutedEvent FindEvent(Interactive obj, string name)
    {
        var matches = RegisteredEvents(obj.GetType()).Where(e => e.Name == name || TypeName(e.OwnerType) + "." + e.Name == name).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException("Unknown or ambiguous routed event.");
    }
    private string Id(AvaloniaObject obj) => _identities.GetValue(obj, _ => new(SessionId + "/" + ++_nextId)).Id;
    private static string TypeName(Type type) => type.FullName ?? type.Name;
    private static string Key(AvaloniaProperty property) => TypeName(property.OwnerType) + "." + property.Name;
    private static string ErrorText(Exception error) => (error is TargetInvocationException { InnerException: { } inner } ? inner : error).Message;
    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args) =>
        Changed(Id(args.Sender), "property", Key(args.Property), DescribeValue(args.NewValue));
    private void Changed(string id, string kind, string name, RuntimeValue? value)
    {
        var change = new RuntimeChange(++_sequence, ++Revision, id, kind, name, value);
        _changes.Enqueue(change);
        while (_changes.Count > 1024) _changes.Dequeue();
        if (RuntimeChanged is { } observers)
            foreach (Action<RuntimeChange> observer in observers.GetInvocationList())
                try { observer(change); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Dispatcher.UIThread.VerifyAccess();
    }
    public void Dispose()
    {
        if (_disposed) return;
        VerifyAccess(); ClearEventWatches(); ClearObjectWatches(); DisposeInput(); DisposeObjectHandles(); DisposeAccessibility();
        foreach (var subscription in _ownedBindings.Values) subscription.Dispose();
        _ownedBindings.Clear();
        foreach (var obj in _objects.Values) obj.PropertyChanged -= OnPropertyChanged;
        _objects.Clear(); _changes.Clear(); _computerFrames.Clear(); RuntimeChanged = null; _disposed = true;
    }
    private sealed record ObjectIdentity(string Id);
}
