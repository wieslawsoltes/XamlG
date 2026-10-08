using System.ComponentModel;

namespace XamlG.Runtime;

/// <summary>Persistent construction frames with target-local adapters and non-reentrant provider fallback.</summary>
public sealed class XamlRuntimeContext : IServiceProvider, IXamlRootObjectProvider, IXamlProvideValueTarget,
    IXamlParentStackProvider, IXamlUriContext, ITypeDescriptorContext
{
    private readonly IServiceProvider? _outer;
    private readonly Func<XamlRuntimeContext, Type, object?>? _services;
    private readonly Func<IServiceProvider, IServiceProvider>? _innerFactory;
    private readonly XamlRuntimeContext? _parent;
    private readonly object? _frameObject;
    private readonly XamlNameScope _names;
    private readonly IReadOnlyDictionary<Type, object>? _namespaces;
    private Dictionary<Type, object>? _localServices;
    private Dictionary<Type, object>? _adapterCache;
    private IReadOnlyList<object>? _directParents;
    private IServiceProvider? _inner;
    private bool _resolvingInner;
    private readonly bool _useTypeDescriptorStubs;
    private readonly XamlRuntimeContext _uriOwner;
    private Uri? _baseUri;
    private object? _root;
    private object? _intermediateRoot;

    public XamlRuntimeContext(IServiceProvider? outer = null, object? root = null, Uri? baseUri = null,
        Func<XamlRuntimeContext, Type, object?>? services = null,
        Func<IServiceProvider, IServiceProvider>? innerFactory = null,
        IReadOnlyDictionary<Type, object>? namespaces = null, bool useTypeDescriptorStubs = false)
    {
        _useTypeDescriptorStubs = useTypeDescriptorStubs;
        _uriOwner = this;
        _outer = outer;
        _root = root;
        _intermediateRoot = root;
        BaseUri = baseUri;
        _services = services;
        _innerFactory = innerFactory;
        _namespaces = namespaces;
        _names = new();
        Session = new();
    }

    private XamlRuntimeContext(XamlRuntimeContext parent, object? frameObject, object? targetObject,
        object? targetProperty, string? nodeKey, bool newScope, IServiceProvider? outer = null,
        IReadOnlyDictionary<Type, object>? namespaces = null)
    {
        _useTypeDescriptorStubs = parent._useTypeDescriptorStubs;
        _uriOwner = newScope ? this : parent._uriOwner;
        if (newScope) _baseUri = parent.BaseUri;
        _outer = outer ?? parent._outer;
        _services = parent._services;
        _innerFactory = parent._innerFactory;
        _namespaces = namespaces ?? parent._namespaces;
        _parent = parent;
        _frameObject = frameObject;
        _root = parent.RootObject;
        _intermediateRoot = newScope ? null : parent.IntermediateRootObject;
        _names = newScope ? new() : parent._names;
        Session = newScope ? new() : parent.Session;
        TargetObject = targetObject;
        TargetProperty = targetProperty;
        NodeKey = newScope ? null : nodeKey ?? parent.NodeKey;
    }

    public XamlRuntimeSession Session { get; }
    public object? RootObject => _root ?? _parent?.RootObject;
    public object? IntermediateRootObject => _intermediateRoot ?? _parent?.IntermediateRootObject;
    public object? TargetObject { get; }
    public object? TargetProperty { get; }
    public string? NodeKey { get; }
    internal object? NodeObject => NodeKey == null ? null : _frameObject ?? _parent?.NodeObject;
    public Uri? BaseUri { get => _uriOwner._baseUri; set => _uriOwner._baseUri = value; }
    public IContainer? Container => null;
    public object? Instance => _useTypeDescriptorStubs ? null : TargetObject;
    public PropertyDescriptor? PropertyDescriptor => null;
    public bool OnComponentChanging() => _useTypeDescriptorStubs ? throw new NotSupportedException() : true;
    public void OnComponentChanged() { if (_useTypeDescriptorStubs) throw new NotSupportedException(); }

    public IEnumerable<object> Parents
    {
        get
        {
            for (var current = this; current != null; current = current._parent)
                if (current._frameObject != null) yield return current._frameObject;
        }
    }

    public IEnumerable<object> EnumerateParents(Type externalContract, Func<object, IEnumerable<object>> selector)
    {
        foreach (var parent in Parents) yield return parent;
        var external = GetExternalService(externalContract);
        if (external != null)
            foreach (var parent in selector(external)) yield return parent;
    }

    /// <summary>Local construction parents in root-to-nearest order, excluding external providers.</summary>
    public IReadOnlyList<object> DirectParentsStack
    {
        get
        {
            if (_frameObject == null) return _parent?.DirectParentsStack ?? Array.Empty<object>();
            if (_directParents != null) return _directParents;
            var count = 0;
            for (var current = this; current != null; current = current._parent)
                if (current._frameObject != null) count++;
            var parents = new object[count];
            for (var current = this; current != null; current = current._parent)
                if (current._frameObject != null) parents[--count] = current._frameObject;
            return _directParents = parents;
        }
    }

    public XamlRuntimeContext PushRoot(object value, string key) => PushRoot(value, key, null);

    public XamlRuntimeContext PushRoot(object value, string key, XamlSourceInfo? source)
    {
        _root = value;
        _intermediateRoot = value;
        return Push(value, key, source);
    }

    public XamlRuntimeContext Push(object value, string key) => Push(value, key, null);

    public XamlRuntimeContext Push(object value, string key, XamlSourceInfo? source)
    {
        if (_root == null && _parent == null) _root = value;
        if (_intermediateRoot == null) _intermediateRoot = value;
        Session.Register(key, value, NodeKey, source);
        return new(this, value, TargetObject, TargetProperty, key, false);
    }

    public XamlRuntimeContext ForTarget(object target, object? property) => new(this, null, target, property, NodeKey, false);

    public XamlRuntimeContext WithNamespaces(IReadOnlyDictionary<Type, object> namespaces) =>
        ReferenceEquals(namespaces, _namespaces) ? this : new(this, null, TargetObject, TargetProperty, NodeKey, false, namespaces: namespaces);

    public XamlRuntimeContext CreateDeferredScope(IServiceProvider? services = null) => new(this, null, TargetObject, TargetProperty, null, true, services);

    public object? GetExternalService(Type serviceType) => _outer?.GetService(serviceType);

    public object? GetNamespaceValue(Type contractType) =>
        _namespaces != null && _namespaces.TryGetValue(contractType, out var value) ? value : null;

    internal object? GetFallbackService(Type serviceType) => GetLocalService(serviceType) ?? GetExternalService(serviceType);

    public object? GetService(Type serviceType)
    {
        if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(XamlRuntimeContext) || serviceType == typeof(ITypeDescriptorContext)) return this;
        if (_innerFactory != null && !_resolvingInner)
        {
            _resolvingInner = true;
            try
            {
                _inner ??= _innerFactory(this);
                var provided = ReferenceEquals(_inner, this) ? null : _inner?.GetService(serviceType);
                if (provided != null) return provided;
            }
            finally { _resolvingInner = false; }
        }
        return GetLocalService(serviceType) ?? GetExternalService(serviceType);
    }

    private object? GetLocalService(Type serviceType)
    {
        if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(XamlRuntimeContext) ||
            serviceType == typeof(IXamlRootObjectProvider) || serviceType == typeof(IXamlProvideValueTarget) ||
            serviceType == typeof(IXamlParentStackProvider) || serviceType == typeof(IXamlUriContext) ||
            serviceType == typeof(ITypeDescriptorContext)) return this;

        for (var frame = this; frame != null; frame = frame._parent)
            if (frame._localServices != null && frame._localServices.TryGetValue(serviceType, out var local)) return local;
        if (_adapterCache != null && _adapterCache.TryGetValue(serviceType, out var cached)) return cached;
        var adapter = _services?.Invoke(this, serviceType);
        if (adapter != null) (_adapterCache ??= new())[serviceType] = adapter;
        return adapter;
    }

    public void AddService(Type contract, object instance)
    {
        if (contract == null) throw new ArgumentNullException(nameof(contract));
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        if (!contract.IsInstanceOfType(instance)) throw new ArgumentException("Service does not implement its contract.", nameof(instance));
        (_localServices ??= new())[contract] = instance;
    }

    public void RegisterName(string name, object value) => _names.Register(name, value);
    public T ResolveName<T>(string name)
    {
        if (_names.TryResolve(name, out var value)) return (T)value;
        if (GetExternalService(typeof(XamlRuntimeContext)) is XamlRuntimeContext outer && !ReferenceEquals(_names, outer._names))
            return outer.ResolveName<T>(name);
        return (T)_names.Resolve(name);
    }
    public void Defer(Action assignment) => _names.Defer(assignment);
    public void Complete(object? root)
    {
        _names.Complete();
        if (root != null) Session.Attach(root);
        else Session.Dispose();
    }
}
