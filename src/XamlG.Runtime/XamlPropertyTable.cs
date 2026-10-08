namespace XamlG.Runtime;

/// <summary>Compiled property dispatch shared by the instances of one generated document.</summary>
public sealed class XamlPropertyTable
{
    private readonly Type[] _types;
    private readonly Func<object, int, object?> _get;
    private readonly Action<object, int, object?> _set;

    public XamlPropertyTable(Type[] types, Func<object, int, object?> get, Action<object, int, object?> set)
    {
        _types = (Type[])(types ?? throw new ArgumentNullException(nameof(types))).Clone();
        if (Array.Exists(_types, static type => type == null)) throw new ArgumentException("Property types cannot be null.", nameof(types));
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _set = set ?? throw new ArgumentNullException(nameof(set));
    }

    public void Register(XamlRuntimeSession session, string key, string member, object target, int index) =>
        (session ?? throw new ArgumentNullException(nameof(session))).RegisterProperty(key, member, new Property(this, target, index));

    private sealed class Property(XamlPropertyTable table, object target, int index) : XamlRuntimeProperty(table._types[index])
    {
        public override object? Get() => table._get(target, index);
        public override void Set(object? value) => table._set(target, index, value);
    }
}
