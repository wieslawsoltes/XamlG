using XamlG.Automation;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiNativeResourceTests
{
    [Fact]
    public void NativeResourceDeclaresOneExplicitFrameOriginAndNoHostAuthority()
    {
        var catalog = new AutomationCatalog(); var store = new UiSessionStore(); using var portable = new UiAutomation(catalog, store);
        UiNativeAppResource.Register(catalog, store, new("https://example.test/XamlG/"));
        var resource = Assert.Single(catalog.Resources, resource => resource.Uri == UiNativeAppResource.ResourceUri);
        Assert.Contains("https://example.test", resource.Metadata!.Value.GetRawText());
        var html = UiNativeAppResource.CreateHtml(new("https://example.test/XamlG/"));
        Assert.Contains("sandbox=\"allow-scripts\"", html); Assert.DoesNotContain("allow-same-origin", html);
        Assert.Contains("https://example.test/XamlG/ui-native.html", html);
        Assert.DoesNotContain("unsafe-eval", html);
    }
    [Theory]
    [InlineData("http://public.test/")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("https://example.test/?secret=1")]
    [InlineData("https://example.test/no-directory")]
    public void RejectsUntrustedDeploymentShapes(string url) => Assert.Throws<ArgumentException>(() => UiNativeAppResource.CreateHtml(new(url)));
}
