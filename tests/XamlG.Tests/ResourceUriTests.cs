using XamlG.Compiler.Resources;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceUriTests
{
    [Fact]
    public void ResourceIdentityIsCaseInsensitiveOnlyForItsAuthority()
    {
        Assert.Equal("avares://sample/Resources/Accent.axaml", XamlResourceUri.Create("avares", "Sample", "Resources/Accent.axaml"));
        Assert.Equal("avares://sample/Resources/A%20B.axaml", XamlResourceUri.Resolve("avares://Sample/Views/Main.axaml", "../Resources/A%20B.axaml"));
        Assert.NotEqual(XamlResourceUri.Normalize("avares://Sample/a.axaml"), XamlResourceUri.Normalize("avares://Sample/A.axaml"));
        Assert.Equal("avares://sample/Root.axaml", XamlResourceUri.Resolve("avares://Sample/Views/Main.axaml", "/Root.axaml"));
    }
    [Theory]
    [InlineData("avares://sample/Path.axaml?query")]
    [InlineData("avares://sample/Path.axaml#fragment")]
    [InlineData("avares://user@sample/Path.axaml")]
    [InlineData("avares://sample/A%2FB.axaml")]
    [InlineData("file:///tmp/View.axaml")]
    public void InvalidAddressesCannotBecomeCatalogKeys(string address) => Assert.Throws<ArgumentException>(() => XamlResourceUri.Normalize(address));
    [Fact]
    public void ExternalFactoryRecursionIsGuardedWithoutGlobalMutableState()
    {
        var context = new XamlRuntimeContext();
        var first = XamlResourceServices.Enter(context, "a");
        var second = XamlResourceServices.Enter(first, "b");
        Assert.Throws<InvalidOperationException>(() => XamlResourceServices.Enter(second, "a"));
        Assert.NotNull(XamlResourceServices.Enter(context, "a"));
        Assert.Same(context, second.GetService(typeof(XamlRuntimeContext)));
    }
}
