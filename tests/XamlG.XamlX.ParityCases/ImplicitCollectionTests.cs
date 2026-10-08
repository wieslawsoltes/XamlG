using System;
using System.Collections;
using System.Collections.Generic;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class ImplicitCollectionTests : CompilerTestBase
{
    private const string Ns = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("Untyped", "object:value")]
    [InlineData("Typed", "string:value")]
    [InlineData("Concrete", "string:value")]
    public void ConfiguredChildInterfacesAreMutationContracts(string property, string expected)
    {
        var root = (ImplicitChildrenRoot)CompileAndRun("<ImplicitChildrenRoot" + Ns + "><ImplicitChildrenRoot." + property + "><x:String>value</x:String></ImplicitChildrenRoot." + property + "></ImplicitChildrenRoot>");
        Assert.Equal(expected, root.Children.Selected);
    }

    [Theory]
    [InlineData("Untyped", "object:value")]
    [InlineData("Typed", "string:value")]
    [InlineData("Concrete", "string:value")]
    public void ProvidedValuesUseTheSameChildInterfaceOrder(string property, string expected)
    {
        var root = (ImplicitChildrenRoot)CompileAndRun("<ImplicitChildrenRoot" + Ns + "><ImplicitChildrenRoot." + property + "><ImplicitChildValue/></ImplicitChildrenRoot." + property + "></ImplicitChildrenRoot>");
        Assert.Equal(expected, root.Children.Selected);
    }

    [Theory]
    [InlineData("<x:String>value</x:String>")]
    [InlineData("<ImplicitChildValue/>")]
    public void OrdinaryInterfaceAddersPrecedeConfiguredChildInterfaces(string content)
    {
        var root = (MixedImplicitChildren)CompileAndRun("<MixedImplicitChildren" + Ns + ">" + content + "</MixedImplicitChildren>");
        Assert.Equal("add:value", root.Selected);
    }

    [Theory]
    [InlineData("Typed")]
    [InlineData("Untyped")]
    public void EnumerablePropertiesUseListMutationContracts(string property)
    {
        var root = (ImplicitEnumerableRoot)CompileAndRun("<ImplicitEnumerableRoot" + Ns + "><ImplicitEnumerableRoot." + property + "><x:String>value</x:String></ImplicitEnumerableRoot." + property + "></ImplicitEnumerableRoot>");
        Assert.Equal(new[] { "value" }, root.Items);
    }

    [Fact]
    public void GenericEnumerableUsesTheDeclaringCollectionMutationInterface()
    {
        var root = (ImplicitSetRoot)CompileAndRun("<ImplicitSetRoot" + Ns + "><x:String>value</x:String></ImplicitSetRoot>");
        Assert.IsType<HashSet<string>>(root.Items);
        Assert.Equal(new[] { "value" }, root.Items);
    }

    [Fact]
    public void DuckTypedAddDoesNotRequireEnumerationOrVoidReturn()
    {
        var root = (ReturningImplicitChildren)CompileAndRun("<ReturningImplicitChildren" + Ns + "><x:String>value</x:String></ReturningImplicitChildren>");
        Assert.Equal("value", root.Selected);
    }

    [Fact]
    public void PrivateInterfaceHelpersAreNotCollectionAdders()
    {
        var root = (HiddenAdderChildren)CompileAndRun("<HiddenAdderChildren" + Ns + "><x:String>value</x:String></HiddenAdderChildren>");
        Assert.Equal("string:value", root.Selected);
    }
}

public sealed class ImplicitChildrenRoot
{
    public ExplicitImplicitChildren Children { get; } = new();
    public IAddChild Untyped => Children;
    public IAddChild<string> Typed => Children;
    public ExplicitImplicitChildren Concrete => Children;
}

public sealed class ExplicitImplicitChildren : IAddChild<string>
{
    public string? Selected { get; private set; }
    void IAddChild.AddChild(object child) => Selected = "object:" + child;
    void IAddChild<string>.AddChild(string child) => Selected = "string:" + child;
}

public interface IImplicitStringAdder
{
    void Add(string value);
}

public sealed class MixedImplicitChildren : IAddChild<string>, IImplicitStringAdder
{
    public string? Selected { get; private set; }
    void IAddChild.AddChild(object child) => Selected = "object:" + child;
    void IAddChild<string>.AddChild(string child) => Selected = "child:" + child;
    void IImplicitStringAdder.Add(string value) => Selected = "add:" + value;
}

public sealed class ImplicitEnumerableRoot
{
    public List<string> Items { get; } = new();
    public IEnumerable<string> Typed => Items;
    public IEnumerable Untyped => Items;
}

public sealed class ImplicitSetRoot
{
    [Content] public IEnumerable<string> Items { get; } = new HashSet<string>();
}

public sealed class ReturningImplicitChildren
{
    public string? Selected { get; private set; }
    public bool Add(string value) { Selected = value; return true; }
}

public sealed class ImplicitChildValue
{
    public object ProvideValue() => "value";
}

public interface IHiddenImplicitAdder
{
    private void Add(string value) { }
}

public sealed class HiddenAdderChildren : IHiddenImplicitAdder, IAddChild<string>
{
    public string? Selected { get; private set; }
    void IAddChild.AddChild(object child) => Selected = "object:" + child;
    void IAddChild<string>.AddChild(string child) => Selected = "string:" + child;
}
