using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class ServiceProviderChainTests
{
    private sealed class Provider(object? value) : IServiceProvider
    {
        public int Calls;
        public object? GetService(Type serviceType) { Calls++; return value; }
    }
    [Fact]
    public void PrimaryServicesWinAndOnlyMissingServicesReachTheCaller()
    {
        var primary = new Provider("framework"); var caller = new Provider("caller");
        Assert.Equal("framework", XamlServiceProviderChain.Combine(primary, caller)!.GetService(typeof(string)));
        Assert.Equal(1, primary.Calls); Assert.Equal(0, caller.Calls);
        var empty = new Provider(null);
        Assert.Equal("caller", XamlServiceProviderChain.Combine(empty, caller)!.GetService(typeof(string)));
        Assert.Equal(1, empty.Calls); Assert.Equal(1, caller.Calls);
    }
    [Fact]
    public void MissingAndIdenticalProvidersDoNotAllocateOrDoubleDispatch()
    {
        var provider = new Provider(null);
        Assert.Null(XamlServiceProviderChain.Combine(null, null));
        Assert.Same(provider, XamlServiceProviderChain.Combine(provider, null));
        Assert.Same(provider, XamlServiceProviderChain.Combine(null, provider));
        var combined = XamlServiceProviderChain.Combine(provider, provider);
        Assert.Same(provider, combined);
        Assert.Null(combined!.GetService(typeof(string))); Assert.Equal(1, provider.Calls);
    }
}
