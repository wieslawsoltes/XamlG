namespace XamlG.Runtime;

/// <summary>A recursion-free view used only while constructing an inner service wrapper.</summary>
internal sealed class XamlFallbackServiceProvider : IServiceProvider
{
    private readonly XamlRuntimeContext _context;

    public XamlFallbackServiceProvider(XamlRuntimeContext context) => _context = context;

    public object? GetService(Type serviceType)
    {
        if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
        return _context.GetFallbackService(serviceType);
    }
}
