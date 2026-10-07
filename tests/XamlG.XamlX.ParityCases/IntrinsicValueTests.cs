using System;
using System.Collections.Generic;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class IntrinsicValueTests : CompilerTestBase
{
    private const string Namespace = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("<x:Static Member='ProbeValues.Number'/>")]
    [InlineData("<x:Static Member='ProbeValues.NumberProperty'/>")]
    [InlineData("<x:Static><x:Static.Member>ProbeValues.Number</x:Static.Member></x:Static>")]
    public void StaticValuesSelectTypedCollectionAdders(string value)
    {
        var root = (NumberCollection)CompileAndRun("<NumberCollection" + Namespace + ">" + value + "</NumberCollection>");
        Assert.Equal(42, root.Value);
    }

    [Fact]
    public void StaticCollectionsCanReplaceWritableProperties()
    {
        var root = (CollectionRoot)CompileAndRun("<CollectionRoot" + Namespace + "><CollectionRoot.Items><x:Static Member='ProbeValues.Strings'/></CollectionRoot.Items></CollectionRoot>");
        Assert.Equal(new[] { "static" }, root.Items);
        Assert.Equal(1, root.Replacements);
    }

    [Fact]
    public void TypeValuesSelectTheTypeOverload()
    {
        var root = (TypeOverloadCollection)CompileAndRun("<TypeOverloadCollection" + Namespace + "><x:Type TypeName='x:String'/></TypeOverloadCollection>");
        Assert.Equal(typeof(string), root.Value);
    }

    [Theory]
    [InlineData("<x:Static Member='ProbeValues.Number'/>")]
    [InlineData("<x:Type TypeName='x:String'/>")]
    public void IntrinsicValuesParticipateInConstructorSelection(string value)
    {
        var root = (ProbeRoot)CompileAndRun("<ProbeRoot" + Namespace + "><ProbeRoot.Child><ProbeConstructor><x:Arguments>" + value + "</x:Arguments></ProbeConstructor></ProbeRoot.Child></ProbeRoot>");
        Assert.Equal(value.Contains("x:Static", StringComparison.Ordinal) ? "number:42" : "type:String", root.Child!.Selected);
    }
}

public static class ProbeValues
{
    public static readonly int Number = 42;
    public static int NumberProperty => 42;
    public static List<string> Strings => new() { "static" };
}

public sealed class NumberCollection
{
    public int Value { get; private set; }
    public void Add(int value) => Value = value;
}

public sealed class TypeOverloadCollection
{
    public object? Value { get; private set; }
    public void Add(string value) => Value = value;
    public void Add(Type value) => Value = value;
}

public sealed class ProbeRoot
{
    public ProbeConstructor? Child { get; set; }
}

public sealed class ProbeConstructor
{
    public string Selected { get; }
    public ProbeConstructor(int value) => Selected = "number:" + value;
    public ProbeConstructor(Type value) => Selected = "type:" + value.Name;
}
