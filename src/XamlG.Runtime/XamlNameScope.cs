namespace XamlG.Runtime;
internal sealed class XamlNameScope
{
    private readonly Dictionary<string, object> _names = new(StringComparer.Ordinal);
    private readonly List<Action> _fixups = new();
    public void Register(string name, object value)
    {
        if (_names.TryGetValue(name, out var previous))
        {
            if (!ReferenceEquals(previous, value)) throw new InvalidOperationException($"Duplicate XAML name '{name}'.");
            return;
        }
        _names.Add(name, value);
    }
    public object Resolve(string name) => _names.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"XAML name '{name}' was not registered.");
    public bool TryResolve(string name, out object value) => _names.TryGetValue(name, out value!);
    public void Defer(Action assignment) => _fixups.Add(assignment);
    public void Complete()
    {
        // Constructing a deferred child can enqueue its own reference assignments.
        // Drain those batches before publishing the completed graph as well.
        while (_fixups.Count != 0)
        {
            var pending = _fixups.ToArray(); _fixups.Clear();
            foreach (var assignment in pending) assignment();
        }
    }
}
