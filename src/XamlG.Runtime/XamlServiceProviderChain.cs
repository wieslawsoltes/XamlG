namespace XamlG.Runtime;

/// <summary>Retains caller services behind a framework root-provider adapter. Services supplied
/// by the framework win; absent services fall back without querying the same provider twice.</summary>
public sealed class XamlServiceProviderChain : IServiceProvider
{
    private readonly IServiceProvider _primary;
    private readonly IServiceProvider _fallback;
    private XamlServiceProviderChain(IServiceProvider primary, IServiceProvider fallback)
    { _primary = primary; _fallback = fallback; }

    public static IServiceProvider? Combine(IServiceProvider? primary, IServiceProvider? fallback) =>
        primary == null ? fallback : fallback == null || ReferenceEquals(primary, fallback) ? primary : new XamlServiceProviderChain(primary, fallback);

    public object? GetService(Type serviceType)
    {
        if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
        return _primary.GetService(serviceType) ?? _fallback.GetService(serviceType);
    }
}
