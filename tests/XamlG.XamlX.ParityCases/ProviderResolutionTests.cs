using System;
using System.Collections.Generic;
using XamlX;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class ProviderResolutionTests : CompilerTestBase
{
    private const string Ns = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("HiddenProvider", "base")]
    [InlineData("VirtualProvider", "override")]
    [InlineData("ParameterlessProvider", "parameterless")]
    [InlineData("ServiceTypedProvider", "typed-service")]
    [InlineData("DualProvider", "extension")]
    public void AttributeProvidersRetainTheSelectedMethod(string name, string expected)
    {
        var root = (ProviderResolutionRoot)CompileAndRun("<ProviderResolutionRoot" + Ns + " Value='{" + name + "}'/>");
        Assert.Equal(expected, root.Value);
    }

    [Theory]
    [InlineData("HiddenProvider", "base")]
    [InlineData("VirtualProvider", "override")]
    [InlineData("ParameterlessProvider", "parameterless")]
    [InlineData("ServiceTypedProvider", "typed-service")]
    [InlineData("DualProvider", "plain")]
    public void ElementProvidersUseElementTypePreference(string name, string expected)
    {
        var root = (ProviderResolutionRoot)CompileAndRun("<ProviderResolutionRoot" + Ns + "><ProviderResolutionRoot.Value><" + name + "/></ProviderResolutionRoot.Value></ProviderResolutionRoot>");
        Assert.Equal(expected, root.Value);
    }

    [Fact]
    public void SelectedProviderTypeIsPreservedInCollectionsAndConstructorArguments()
    {
        var root = (ProviderResolutionRoot)CompileAndRun("<ProviderResolutionRoot" + Ns + "><ProviderResolutionRoot.Items><HiddenProvider/></ProviderResolutionRoot.Items><ProviderResolutionRoot.Argument><ProviderArgument><x:Arguments><HiddenProvider/></x:Arguments></ProviderArgument></ProviderResolutionRoot.Argument></ProviderResolutionRoot>");
        Assert.Equal("base", Assert.Single(root.Items));
        Assert.Equal("base", root.Argument!.Value);
    }

    [Theory]
    [InlineData("MissingProvider")]
    [InlineData("MissingProviderExtension")]
    public void ExtensionSuffixRequiresAProviderInElementForm(string name)
    {
        Assert.ThrowsAny<XamlParseException>(() => Compile("<ProviderResolutionRoot" + Ns + "><ProviderResolutionRoot.Value><" + name + "/></ProviderResolutionRoot.Value></ProviderResolutionRoot>"));
    }
}

public sealed class ProviderResolutionRoot
{
    public object? Value { get; set; }
    public List<string> Items { get; } = new();
    public ProviderArgument? Argument { get; set; }
}
public sealed class ProviderArgument(string value) { public string Value { get; } = value; }
public class ProviderBase { public string ProvideValue() => "base"; }
public sealed class HiddenProvider : ProviderBase { public new object ProvideValue() => "hidden"; }
public class VirtualProviderBase { public virtual string ProvideValue() => "base"; }
public sealed class VirtualProvider : VirtualProviderBase { public override string ProvideValue() => "override"; }
public sealed class ParameterlessProvider
{
    public object ProvideValue() => "parameterless";
    public string ProvideTypedValue(IServiceProvider services) => "typed-service";
}
public sealed class ServiceTypedProvider
{
    public object ProvideValue(IServiceProvider services) => "object-service";
    public string ProvideTypedValue(IServiceProvider services) => "typed-service";
}
public sealed class DualProvider { public string ProvideValue() => "plain"; }
public sealed class DualProviderExtension { public string ProvideValue() => "extension"; }
public sealed class MissingProviderExtension { }
