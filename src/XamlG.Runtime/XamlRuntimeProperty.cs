namespace XamlG.Runtime;
internal sealed class XamlRuntimeProperty
{
    public XamlRuntimeProperty(Type type, Func<object?> get, Action<object?> set) { Type = type; Get = get; Set = set; }
    public Type Type { get; }
    public Func<object?> Get { get; }
    public Action<object?> Set { get; }
    public bool Accepts(object? value) => value == null ? !Type.IsValueType || Nullable.GetUnderlyingType(Type) != null : (Nullable.GetUnderlyingType(Type) ?? Type).IsInstanceOfType(value);
}
