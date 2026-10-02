namespace XamlG.Runtime;
internal sealed class XamlNameScope
{
    private readonly Dictionary<string, object> _names = new(StringComparer.Ordinal);
    private readonly List<Action> _fixups = new();
    public void Register(string name, object value)
    {
        if (_names.ContainsKey(name)) throw new InvalidOperationException($"Duplicate XAML name '{name}'.");
        _names.Add(name, value);
    }
    public object Resolve(string name) => _names.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"XAML name '{name}' was not registered.");
    public void Defer(Action assignment) => _fixups.Add(assignment);
    public void Complete()
    {
        var pending = _fixups.ToArray(); _fixups.Clear();
        foreach (var assignment in pending) assignment();
    }
}
