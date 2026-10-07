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

    [Fact]
    public void StaticAdditionsReadTheCollectionForEachValue()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.Items><x:String>one</x:String><x:String>two</x:String></CollectionRoot.Items></CollectionRoot>");
        Assert.Equal(new[] { "get", "get" }, root.Events);
        Assert.Equal(new[] { "initial", "one", "two" }, root.Items);
    }

    [Fact]
    public void StaticallySelectedAddersReadBeforeEvaluatingTheValue()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.Items><StringValue/></CollectionRoot.Items></CollectionRoot>");
        Assert.Equal(new[] { "get", "provide:string", "set" }, root.Events);
        Assert.Equal(new[] { "changed" }, root.Items);
    }

    [Fact]
    public void RuntimeSelectedAddersReadAfterEvaluatingTheValue()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.ReadOnlyItems><RuntimeValue Mode='mutate'/></CollectionRoot.ReadOnlyItems></CollectionRoot>");
        Assert.Equal(new[] { "provide:mutate", "set", "get" }, root.Events);
        Assert.Equal(new[] { "changed", "item" }, root.Items);
    }

    [Fact]
    public void AProvidedObjectWithOneAdderReadsBeforeEvaluatingTheValue()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.SingleItems><RuntimeValue Mode='item'/></CollectionRoot.SingleItems></CollectionRoot>");
        Assert.Equal(new[] { "get:single", "provide:item" }, root.Events);
    }

    [Fact]
    public void AnUnmatchedRuntimeValueDoesNotReadTheCollection()
    {
        var root = new CollectionRoot();
        Assert.Throws<InvalidCastException>(() => CompileAndPopulate(Prefix + "<CollectionRoot.OverloadedItems><RuntimeValue Mode='invalid'/></CollectionRoot.OverloadedItems></CollectionRoot>", instance: root));
        Assert.Equal(new[] { "provide:invalid" }, root.Events);
    }

    [Fact]
    public void RuntimeKeyedAddersReadAfterBothArguments()
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot.Entries><RuntimeValue Mode='item' x:Key='{KeyValue}'/></CollectionRoot.Entries></CollectionRoot>");
        Assert.Equal(new[] { "provide:key", "provide:item", "get:entries" }, root.Events);
    }

    [Theory]
    [InlineData("ObjectItems")]
    [InlineData("InterfaceItems")]
    public void RedundantRuntimeAlternativesUseTheStaticGetterOrder(string property)
    {
        var root = (CollectionRoot)CompileAndRun(Prefix + "<CollectionRoot." + property + "><RuntimeValue Mode='item'/></CollectionRoot." + property + "></CollectionRoot>");
        Assert.Equal(new[] { "get:" + property, "provide:item" }, root.Events);
    }
}

public sealed class CollectionRoot
{
    private List<string>? _items = new() { "initial" };
    public List<string> Events { get; } = new();
    public int Replacements { get; private set; }
    public List<string>? ReadOnlyItems => Items;
    public TypedCollection SingleItems { get { Events.Add("get:single"); return new(); } }
    public OverloadedCollection OverloadedItems { get { Events.Add("get:overloaded"); return new(); } }
    public KeyedCollection Entries { get { Events.Add("get:entries"); return new(); } }
    public ObjectFirstCollection ObjectItems { get { Events.Add("get:ObjectItems"); return new(); } }
    public InterfaceStringCollection InterfaceItems { get { Events.Add("get:InterfaceItems"); return new(); } }
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

public sealed class OverloadedCollection
{
    public void Add(int value) { }
    public void Add(string value) { }
}

public interface IStringItems
{
    void Add(string value);
}

public sealed class InterfaceStringCollection : IStringItems
{
    public void Add(string value) { }
}

public sealed class KeyedCollection
{
    public void Add(int key, int value) { }
    public void Add(int key, string value) { }
}

public sealed class KeyValue
{
    public int ProvideValue(IServiceProvider services)
    {
        var root = (CollectionRoot)((ITestRootObjectProvider)services.GetService(typeof(ITestRootObjectProvider))!).RootObject!;
        root.Events.Add("provide:key");
        return 1;
    }
}

public sealed class StringValue
{
    public string ProvideValue(IServiceProvider services)
    {
        var root = (CollectionRoot)((ITestRootObjectProvider)services.GetService(typeof(ITestRootObjectProvider))!).RootObject!;
        root.Events.Add("provide:string");
        root.Items = new List<string> { "changed" };
        return "item";
    }
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
        if (Mode == "invalid") return true;
        return Mode == "null" ? null : "item";
    }
}
