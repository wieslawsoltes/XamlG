namespace XamlG.Runtime;
internal abstract class XamlRuntimeProperty(Type type)
{
    public Type Type { get; } = type;
    public abstract object? Get(object? target);
    public abstract void Set(object? target, object? value);
    public bool Accepts(object? value) => value == null ? !Type.IsValueType || Nullable.GetUnderlyingType(Type) != null : (Nullable.GetUnderlyingType(Type) ?? Type).IsInstanceOfType(value);
}

internal sealed class XamlDelegateProperty<T>(Func<T> get, Action<T> set) : XamlRuntimeProperty(typeof(T))
{
    public override object? Get(object? target) => get();
    public override void Set(object? target, object? value) => set((T)value!);
}

// Two references, stored inline in the session's table-backed property dictionary.
// The shared accessor never captures a target; session ownership remains local.
internal readonly struct XamlRuntimePropertyBinding(XamlRuntimeProperty accessor, object? target = null)
{
    public Type Type => accessor.Type;
    public object? Get() => accessor.Get(target);
    public void Set(object? value) => accessor.Set(target, value);
    public bool Accepts(object? value) => accessor.Accepts(value);
}
