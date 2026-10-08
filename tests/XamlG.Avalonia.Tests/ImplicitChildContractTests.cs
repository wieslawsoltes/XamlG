using Avalonia.Headless.XUnit;
using Avalonia.Metadata;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ImplicitChildContractTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("ObjectItems", false, "object:value")]
    [InlineData("ObjectItems", true, "object:value")]
    [InlineData("StringItems", false, "string:value")]
    [InlineData("StringItems", true, "string:value")]
    [InlineData("ConcreteItems", false, "string:value")]
    [InlineData("ConcreteItems", true, "string:value")]
    public void ExplicitChildInterfacesUseTheDeclaredContract(string property, bool provided, string expected)
    {
        var value = provided ? "<t:ImplicitAvaloniaChildValue/>" : "<x:String>value</x:String>";
        var xaml = "<t:ImplicitAvaloniaCollectionRoot " + Ns + "><t:ImplicitAvaloniaCollectionRoot." + property + ">" + value +
            "</t:ImplicitAvaloniaCollectionRoot." + property + "></t:ImplicitAvaloniaCollectionRoot>";
        foreach (var root in CompileBoth<ImplicitAvaloniaCollectionRoot>(xaml)) Assert.Equal(expected, root.Children.Selected);
    }

    [AvaloniaTheory]
    [InlineData("value")]
    [InlineData("<t:ImplicitAvaloniaChildValue/>")]
    public void ImplicitContentUsesTheTypedChildContract(string value)
    {
        var xaml = "<t:ImplicitAvaloniaCollectionRoot " + Ns + ">" + value + "</t:ImplicitAvaloniaCollectionRoot>";
        foreach (var root in CompileBoth<ImplicitAvaloniaCollectionRoot>(xaml)) Assert.Equal("string:value", root.Children.Selected);
    }

    [AvaloniaTheory]
    [InlineData("value")]
    [InlineData("<t:ImplicitAvaloniaChildValue/>")]
    public void OrdinaryAdderInterfacesPrecedeChildProtocols(string value)
    {
        var xaml = "<t:MixedAvaloniaChildCollection " + Ns + ">" + value + "</t:MixedAvaloniaChildCollection>";
        foreach (var root in CompileBoth<MixedAvaloniaChildCollection>(xaml)) Assert.Equal("add:value", root.Selected);
    }

    private static IEnumerable<T> CompileBoth<T>(string xaml)
    {
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsType<T>(baseline.Root);
        yield return Assert.IsType<T>(new ResourceProjectFixture(new[] { ("Children.axaml", xaml) }).Build("Children.axaml"));
    }
}

public sealed class ImplicitAvaloniaCollectionRoot
{
    public ExplicitAvaloniaChildren Children { get; } = new();
    public IAddChild ObjectItems => Children;
    [Content] public IAddChild<string> StringItems => Children;
    public ExplicitAvaloniaChildren ConcreteItems => Children;
}

public sealed class ExplicitAvaloniaChildren : IAddChild, IAddChild<string>
{
    public string? Selected { get; private set; }
    void IAddChild.AddChild(object child) => Selected = "object:" + child;
    void IAddChild<string>.AddChild(string child) => Selected = "string:" + child;
}

public interface IOrdinaryAvaloniaAdder
{
    void Add(string child);
}

public sealed class MixedAvaloniaChildCollection : IAddChild, IAddChild<string>, IOrdinaryAvaloniaAdder
{
    public string? Selected { get; private set; }
    void IAddChild.AddChild(object child) => Selected = "object:" + child;
    void IAddChild<string>.AddChild(string child) => Selected = "child:" + child;
    void IOrdinaryAvaloniaAdder.Add(string child) => Selected = "add:" + child;
}

public sealed class ImplicitAvaloniaChildValue
{
    public object ProvideValue() => "value";
}
