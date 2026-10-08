namespace XamlG.Runtime;
internal abstract class XamlRuntimeProperty(Type type)
{
    public Type Type { get; } = type;
    public abstract object? Get();
    public abstract void Set(object? value);
    public bool Accepts(object? value) => value == null ? !Type.IsValueType || Nullable.GetUnderlyingType(Type) != null : (Nullable.GetUnderlyingType(Type) ?? Type).IsInstanceOfType(value);
}

internal sealed class XamlDelegateProperty<T>(Func<T> get, Action<T> set) : XamlRuntimeProperty(typeof(T))
{
    public override object? Get() => get();
    public override void Set(object? value) => set((T)value!);
}
