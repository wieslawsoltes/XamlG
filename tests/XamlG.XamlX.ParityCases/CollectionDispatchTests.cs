using System;
using System.Collections.Generic;
using XamlX;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class CollectionDispatchTests : CompilerTestBase
{
    private const string Prefix = "<CollectionRoot xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>";

    [Theory]
    [InlineData("collection", "provided", 1)]
    [InlineData("item", "initial,item", 0)]
    [InlineData("mutate", "changed,item", 1)]
    public void RuntimeValueSelectsReplacementOrAddition(string mode, string expected, int replacements)
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.Items><RuntimeValue Mode='" + mode + "'/></CollectionRoot.Items></CollectionRoot>");
        Assert.Equal(expected, string.Join(",", root.Items!));
        Assert.Equal(replacements, root.Replacements);
    }

    [Fact]
    public void ReplacementDoesNotReadThePreviousCollection()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.Items><RuntimeValue Mode='collection'/></CollectionRoot.Items></CollectionRoot>");
        Assert.Equal(new[] { "provide:collection", "set" }, root.Events);
    }

    [Fact]
    public void ReplacementPrecedesSubsequentAdditions()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.Items><RuntimeValue Mode='collection'/><x:String>after</x:String></CollectionRoot.Items></CollectionRoot>");
        Assert.Equal(new[] { "provided", "after" }, root.Items);
        Assert.Equal(1, root.Replacements);
    }

    [Fact]
    public void NullUsesTheObjectAdderFallback()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.Items><RuntimeValue Mode='null'/></CollectionRoot.Items></CollectionRoot>");
        Assert.Equal(new string?[] { "initial", null }, root.Items);
        Assert.Equal(0, root.Replacements);
    }

    [Fact]
    public void ExplicitNullReplacesTheCollection()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.Items><x:Null/></CollectionRoot.Items></CollectionRoot>");
        Assert.Null(root.Items);
        Assert.Equal(1, root.Replacements);
    }

    [Fact]
    public void RuntimeNullWithoutAnObjectAdderUsesTheFirstNullableSetter()
    {
        var root = (TypedCollectionRoot)CompileAndRun("<TypedCollectionRoot xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests'><TypedCollectionRoot.Items><NullValue/></TypedCollectionRoot.Items></TypedCollectionRoot>");
        Assert.Null(root.Items);
    }

    [Fact]
    public void SubsequentValuesCannotReplaceTheCollection()
    {
        Assert.Throws<ArgumentException>(() => CompileAndRun(Prefix + "<CollectionRoot.Items><RuntimeValue Mode='item'/><RuntimeValue Mode='collection'/></CollectionRoot.Items></CollectionRoot>"));
    }

    [Fact]
    public void StaticallyTypedCollectionItemsUseTheNonGenericFallback()
    {
        Assert.Throws<ArgumentException>(() => CompileAndRun(Prefix + "<CollectionRoot.Items><StringItems/><StringItems/></CollectionRoot.Items></CollectionRoot>"));
    }

    [Fact]
    public void AttachedCollectionsUseTheSameRuntimeAlternatives()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionAccess.Items><RuntimeValue Mode='collection'/><x:String>after</x:String></CollectionAccess.Items></CollectionRoot>");
        Assert.Equal(new[] { "provided", "after" }, root.Items);
        Assert.Equal(1, root.Replacements);
    }

    [Theory]
    [InlineData("{RuntimeValue Mode=item}")]
    [InlineData("{RuntimeValue Mode=collection}")]
    public void AttributeExtensionsCannotAddToReadOnlyCollections(string value)
    {
        Assert.Throws<XamlLoadException>(() => Compile("<CollectionRoot xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' ReadOnlyItems='" + value + "'/>"));
    }
}

public sealed class CollectionRoot
{
    private List<string>? _items = new() { "initial" };
    public List<string> Events { get; } = new();
    public int Replacements { get; private set; }
    public List<string>? ReadOnlyItems => _items;
    public List<string>? Items
    {
        get { Events.Add("get"); return _items; }
        set { Events.Add("set"); _items = value; Replacements++; }
    }
}

public static class CollectionAccess
{
    public static List<string>? GetItems(CollectionRoot root) => root.Items;
    public static void SetItems(CollectionRoot root, List<string>? items) => root.Items = items;
}

public sealed class StringItems : List<string> { }

public sealed class TypedCollectionRoot
{
    public TypedCollection? Items { get; set; } = new();
}

public sealed class TypedCollection
{
    public void Add(string value) { }
}

public sealed class NullValue
{
    public object? ProvideValue() => null;
}

public sealed class RuntimeValue
{
    public string? Mode { get; set; }
    public object? ProvideValue(IServiceProvider services)
    {
        var root = (CollectionRoot)((ITestRootObjectProvider)services.GetService(typeof(ITestRootObjectProvider))!).RootObject!;
        root.Events.Add("provide:" + Mode);
        if (Mode == "collection") return new List<string> { "provided" };
        if (Mode == "mutate") root.Items = new List<string> { "changed" };
        return Mode == "null" ? null : "item";
    }
}
