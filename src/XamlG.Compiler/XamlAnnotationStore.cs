namespace XamlG.Compiler;
public sealed class XamlAnnotationStore
{
    private readonly Dictionary<object, object?> _values = new();
    public void Set<T>(XamlAnnotationKey<T> key, T value) => _values[key] = value;
    public bool TryGet<T>(XamlAnnotationKey<T> key, out T value)
    {
        if (_values.TryGetValue(key, out var boxed) && boxed is T typed) { value = typed; return true; }
        value = default!; return false;
    }
}
