namespace XamlG.Runtime;

/// <summary>A persistent include stack prevents recursive external factories without shared mutable/global state.</summary>
public sealed class XamlResourceServices : IServiceProvider
{
    private readonly IServiceProvider _services;
    private readonly XamlResourceServices? _parent;
    private readonly string _uri;
    private XamlResourceServices(IServiceProvider services, string uri, XamlResourceServices? parent)
    { _services = services; _uri = uri; _parent = parent; }
    public static IServiceProvider Enter(IServiceProvider services, string uri)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (uri == null) throw new ArgumentNullException(nameof(uri));
        var parent = services.GetService(typeof(XamlResourceServices)) as XamlResourceServices;
        var depth = 0;
        for (var current = parent; current != null; current = current._parent)
        {
            if (++depth > 128) throw new InvalidOperationException("Compiled resource nesting exceeds 128 factories.");
            if (StringComparer.Ordinal.Equals(current._uri, uri)) throw new InvalidOperationException("Recursive compiled resource include: " + uri);
        }
        return new XamlResourceServices(services, uri, parent);
    }
    public object? GetService(Type serviceType) => serviceType == typeof(XamlResourceServices) ? this : _services.GetService(serviceType);
}
