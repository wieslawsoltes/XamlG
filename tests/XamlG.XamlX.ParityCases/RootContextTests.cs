using System;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class RootContextTests : CompilerTestBase
{
    private const string Ns = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructedRootsSupersedeExternalRootProviders(bool populate)
    {
        var xaml = "<RootContextRoot" + Ns + " Value='{RootContextProbe}'><RootContextRoot.Child><RootContextRoot Value='{RootContextProbe}'/></RootContextRoot.Child></RootContextRoot>";
        var services = new ExternalRootContext();
        RootContextRoot root;
        if (populate) { root = new(); CompileAndPopulate(xaml, services, root); }
        else root = (RootContextRoot)CompileAndRun(xaml, services);
        Assert.Same(root, root.Value);
        Assert.Same(root, root.Child!.Value);
    }

    [Fact]
    public void ConstructorArgumentObjectsDoNotBecomeTheDocumentRoot()
    {
        var root = (RootArgumentContainer)CompileAndRun("<RootArgumentContainer" + Ns + " Value='{RootContextProbe}'><x:Arguments><RootContextRoot/></x:Arguments></RootArgumentContainer>");
        Assert.Same(root, root.Value);
    }
}

public class RootContextRoot
{
    public object? Value { get; set; }
    public RootContextRoot? Child { get; set; }
}
public sealed class RootArgumentContainer(RootContextRoot argument) : RootContextRoot
{
    public RootContextRoot Argument { get; } = argument;
}
public sealed class RootContextProbe
{
    public object? ProvideValue(IServiceProvider services) => ((ITestRootObjectProvider)services.GetService(typeof(ITestRootObjectProvider))!).RootObject;
}
public sealed class ExternalRootContext : IServiceProvider, ITestRootObjectProvider
{
    public object RootObject { get; } = new();
    public object? GetService(Type serviceType) => serviceType == typeof(ITestRootObjectProvider) ? this : null;
}
