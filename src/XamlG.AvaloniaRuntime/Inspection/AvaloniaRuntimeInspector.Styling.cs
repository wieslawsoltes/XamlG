using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Styling;
using System.Collections;
using System.Reflection;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed partial class AvaloniaRuntimeInspector
{
    private readonly Dictionary<(string ObjectId, string Property), IDisposable> _ownedBindings = new();

    public IReadOnlyList<RuntimeValueFrame> ValueFrames(string objectId)
    {
        var obj = Resolve(objectId);
        // Avalonia exposes these diagnostics on runtime assemblies but omits their members
        // from its reference assemblies. Keep this version-sensitive reflection isolated.
        var diagnostic = obj.GetValueStoreDiagnostic();
        var frames = (DiagnosticMember(diagnostic, "AppliedFrames") as IEnumerable)?.Cast<object>().Take(10001).ToArray()
            ?? throw new InvalidOperationException("The loaded Avalonia runtime does not expose applied-frame diagnostics.");
        if (frames.Length > 10000) throw new InvalidOperationException("Too many applied value frames.");
        return frames.Select((frame, index) =>
        {
            var source = DiagnosticMember(frame, "Source");
            var values = (DiagnosticMember(frame, "Values") as IEnumerable)?.Cast<object>().Take(1001).ToArray() ?? [];
            if (values.Length > 1000) throw new InvalidOperationException("Too many values in a diagnostic frame.");
            return new RuntimeValueFrame(index, DiagnosticMember(frame, "Type")?.ToString() ?? "unknown", DiagnosticMember(frame, "Priority")?.ToString() ?? "unknown",
                DiagnosticMember(frame, "IsActive") is true, source == null ? "null" : TypeName(source.GetType()), source is Style style ? style.Selector?.ToString() : null,
                values.Select(value => new RuntimeFrameValue(Key((AvaloniaProperty)DiagnosticMember(value, "Property")!), DescribeValue(DiagnosticMember(value, "Value")))).ToArray());
        }).ToArray();
    }

    public IReadOnlyList<RuntimeBinding> Bindings(string objectId)
    {
        var obj = Resolve(objectId); var bindings = new List<RuntimeBinding>();
        foreach (var property in Registered(obj))
        {
            var binding = BindingOperations.GetBindingExpressionBase(obj, property);
            if (binding == null) continue;
            bindings.Add(new(Key(property), TypeName(binding.GetType()), DiagnosticMember(binding, "Description") as string, DiagnosticMember(binding, "ErrorType")?.ToString(),
                DiagnosticMember(binding, "IsRunning") as bool?, DiagnosticMember(binding, "Priority")?.ToString(), DescribeValue(obj.GetValue(property))));
        }
        return bindings;
    }

    public RuntimeBinding SetBinding(string objectId, string propertyKey, string path, string mode, RuntimeArgument? source, long expectedRevision)
    {
        if (path.Length > 4096 || !Enum.TryParse<BindingMode>(mode, false, out var bindingMode) || !Enum.IsDefined(bindingMode)) throw new ArgumentException("Invalid binding path or mode.");
        var obj = ResolveForMutation(objectId, expectedRevision); var property = FindProperty(obj, propertyKey);
        if (property.IsReadOnly) throw new InvalidOperationException("The property is read-only.");
        var binding = new Binding(path) { Mode = bindingMode };
        if (source != null) binding.Source = ConvertArgument(source, typeof(object));
        if (_ownedBindings.Count >= 4096 && !_ownedBindings.ContainsKey((objectId, Key(property)))) throw new InvalidOperationException("The inspection session binding limit is reached.");
        ResolveForMutation(objectId, expectedRevision);
        // Avalonia constructs the new expression before replacing the current value. Keep
        // the previous lease until that succeeds, so malformed paths do not clear it.
        var replacement = obj.Bind(property, binding);
        RetireBinding(objectId, Key(property));
        _ownedBindings[(objectId, Key(property))] = replacement;
        Changed(objectId, "binding", Key(property), DescribeValue(path));
        return Bindings(objectId).Single(item => item.Property == Key(property));
    }

    public void UpdateBinding(string objectId, string propertyKey, bool updateSource, long expectedRevision)
    {
        var obj = ResolveForMutation(objectId, expectedRevision); var property = FindProperty(obj, propertyKey);
        var expression = BindingOperations.GetBindingExpressionBase(obj, property) ?? throw new InvalidOperationException("No active binding expression.");
        if (updateSource) expression.UpdateSource(); else expression.UpdateTarget();
        Changed(objectId, "binding_update", Key(property), DescribeValue(updateSource ? "source" : "target"));
    }

    public IReadOnlyList<RuntimeStyle> Styles(string objectId)
    {
        var obj = Resolve(objectId) as StyledElement ?? throw new InvalidOperationException("The object is not styled.");
        if (obj.Styles.Count > 10000) throw new InvalidOperationException("Too many local styles.");
        return obj.Styles.Select((item, index) => new RuntimeStyle(index, TypeName(item.GetType()), (item as Style)?.Selector?.ToString(),
            item is StyleBase style ? style.Setters.OfType<Setter>().Take(1000).Where(setter => setter.Property != null)
                .Select(setter => new RuntimeFrameValue(Key(setter.Property!), DescribeValue(setter.Value))).ToArray() : [])).ToArray();
    }

    public int AddStyle(string objectId, string targetType, string? className, IReadOnlyDictionary<string, RuntimeArgument> setters, long expectedRevision)
    {
        var obj = ResolveForMutation(objectId, expectedRevision) as StyledElement ?? throw new InvalidOperationException("The object is not styled.");
        if (obj.Styles.Count >= 10000) throw new InvalidOperationException("The local style limit is reached.");
        var type = ResolveType(targetType);
        if (!typeof(StyledElement).IsAssignableFrom(type)) throw new ArgumentException("The style target must be a StyledElement.");
        if (setters.Count is < 1 or > 256 || className?.Any(char.IsWhiteSpace) == true || className?.Length > 256) throw new ArgumentException("Invalid style class or setter count.");
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        var properties = AvaloniaPropertyRegistry.Instance.GetRegistered(type).Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(type)).Distinct().ToArray();
        var style = new Style(selector => string.IsNullOrEmpty(className) ? selector.OfType(type) : selector.OfType(type).Class(className));
        foreach (var (key, argument) in setters)
        {
            var matches = properties.Where(property => Key(property) == key || property.Name == key).ToArray();
            if (matches.Length != 1 || matches[0].IsReadOnly || matches[0].IsDirect) throw new ArgumentException("Use an unambiguous writable styled property key.");
            style.Setters.Add(new Setter(matches[0], ConvertArgument(argument, matches[0].PropertyType)));
        }
        ResolveForMutation(objectId, expectedRevision); obj.Styles.Add(style);
        if (obj is Control control) control.UpdateLayout();
        Changed(objectId, "style", "add", DescribeValue(style.Selector?.ToString())); return obj.Styles.Count - 1;
    }

    public void RemoveStyle(string objectId, int index, long expectedRevision)
    {
        var obj = ResolveForMutation(objectId, expectedRevision) as StyledElement ?? throw new InvalidOperationException("The object is not styled.");
        if (index < 0 || index >= obj.Styles.Count) throw new ArgumentOutOfRangeException(nameof(index));
        obj.Styles.RemoveAt(index);
        if (obj is Control control) control.UpdateLayout();
        Changed(objectId, "style", "remove", DescribeValue(index));
    }

    private void RetireBindings(string objectId)
    { foreach (var key in _ownedBindings.Keys.Where(key => key.ObjectId == objectId).ToArray()) RetireBinding(key.ObjectId, key.Property); }
    private void RetireBinding(string objectId, string property)
    { if (_ownedBindings.Remove((objectId, property), out var subscription)) subscription.Dispose(); }
    private static object? DiagnosticMember(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
}
