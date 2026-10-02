namespace XamlG.Runtime;

/// <summary>Persistent service-provider frames preserve parent and target semantics across nested and deferred construction.</summary>
public sealed class XamlRuntimeContext : IServiceProvider, IXamlRootObjectProvider, IXamlProvideValueTarget, IXamlParentStackProvider, IXamlUriContext
{
    private readonly IServiceProvider? _outer;
    private readonly Func<XamlRuntimeContext, Type, object?>? _services;
    private readonly XamlRuntimeContext? _parent;
    private readonly object? _frameObject;
    private readonly XamlNameScope _names;
    private readonly Dictionary<Type, object> _localServices = new();
    private object? _root;
    private object? _intermediateRoot;
    public XamlRuntimeContext(IServiceProvider? outer = null, object? root = null, Uri? baseUri = null, Func<XamlRuntimeContext, Type, object?>? services = null)
    { _outer = outer; _root = root; _intermediateRoot = root; BaseUri = baseUri; _services = services; _names = new(); Session = new(); }
    private XamlRuntimeContext(XamlRuntimeContext parent, object? frameObject, object? targetObject, object? targetProperty, string? nodeKey, bool newScope)
    {
        _outer = parent._outer; _services = parent._services; _parent = parent; _frameObject = frameObject;
        _root = parent.RootObject; _intermediateRoot = newScope ? null : parent.IntermediateRootObject;
        _names = newScope ? new() : parent._names; Session = newScope ? new() : parent.Session;
        BaseUri = parent.BaseUri; TargetObject = targetObject; TargetProperty = targetProperty; NodeKey = nodeKey ?? parent.NodeKey;
    }
    public XamlRuntimeSession Session { get; }
    public object? RootObject => _root ?? _parent?.RootObject;
    public object? IntermediateRootObject => _intermediateRoot ?? _parent?.IntermediateRootObject;
    public object? TargetObject { get; }
    public object? TargetProperty { get; }
    public string? NodeKey { get; }
    public Uri? BaseUri { get; set; }
    public IEnumerable<object> Parents
    {
        get { for (var current = this; current != null; current = current._parent) if (current._frameObject != null) yield return current._frameObject; }
    }
    public XamlRuntimeContext Push(object value, string key)
    {
        if (_root == null && _parent == null) _root = value;
        if (_intermediateRoot == null) _intermediateRoot = value;
        Session.Register(key, value, NodeKey);
        return new(this, value, TargetObject, TargetProperty, key, false);
    }
    public XamlRuntimeContext ForTarget(object target, object? property) => new(this, null, target, property, NodeKey, false);
    public XamlRuntimeContext CreateDeferredScope(IServiceProvider? services = null) => new(this, null, TargetObject, TargetProperty, null, true);
    public object? GetService(Type serviceType)
    {
        if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(XamlRuntimeContext) || serviceType == typeof(IXamlRootObjectProvider) || serviceType == typeof(IXamlProvideValueTarget) || serviceType == typeof(IXamlParentStackProvider) || serviceType == typeof(IXamlUriContext)) return this;
        if (_localServices.TryGetValue(serviceType, out var local)) return local;
        for (var parent = _parent; parent != null; parent = parent._parent) if (parent._localServices.TryGetValue(serviceType, out local)) return local;
        return _services?.Invoke(this, serviceType) ?? _outer?.GetService(serviceType);
    }
    public void AddService(Type contract, object instance) => _localServices[contract] = instance;
    public void RegisterName(string name, object value) => _names.Register(name, value);
    public T ResolveName<T>(string name) => (T)_names.Resolve(name);
    public void Defer(Action assignment) => _names.Defer(assignment);
    public void Complete(object root) { _names.Complete(); Session.Attach(root); }
}
