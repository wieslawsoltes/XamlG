using System;
using System.Collections.Generic;
using XamlX;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class ConstructorDispatchTests : CompilerTestBase
{
    private const string Namespace = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("ObjectFirstConstructor", "<x:String>text</x:String>", "object")]
    [InlineData("ObjectFirstConstructor", "<TextValue/>", "object")]
    [InlineData("ConvertedConstructor", "<x:String>42</x:String>", "int")]
    public void ConstructorsKeepTheFirstCompatibleSignature(string type, string argument, string expected)
    {
        var root = (ConstructorContainer)CompileAndRun("<ConstructorContainer" + Namespace + "><ConstructorContainer.Value><" + type + "><x:Arguments>" + argument + "</x:Arguments></" + type + "></ConstructorContainer.Value></ConstructorContainer>");
        Assert.Equal(expected, ((ConstructorSelection)root.Value!).Selected);
    }

    [Fact]
    public void AFailedFirstConversionDoesNotSelectAnotherConstructor()
    {
        Assert.ThrowsAny<XamlParseException>(() => Compile("<ConstructorContainer" + Namespace + "><ConstructorContainer.Value><ConvertedConstructor><x:Arguments><x:String>invalid</x:String></x:Arguments></ConvertedConstructor></ConstructorContainer.Value></ConstructorContainer>"));
    }

    [Fact]
    public void ConstructorArgumentsDoNotUseObjectDowncasts()
    {
        Assert.ThrowsAny<XamlParseException>(() => Compile("<ConstructorContainer" + Namespace + "><ConstructorContainer.Value><NumberArgument><x:Arguments><IntegerValue/></x:Arguments></NumberArgument></ConstructorContainer.Value></ConstructorContainer>"));
    }

    [Fact]
    public void ArgumentsFinishEvaluationBeforeTheNextArgumentStarts()
    {
        ConstructorOrder.Events.Clear();
        CompileAndRun("<OrderedConstructor" + Namespace + "><x:Arguments><OrderedTextValue/><LaterArgument/></x:Arguments></OrderedConstructor>");
        Assert.Equal(new[] { "provide", "construct", "root" }, ConstructorOrder.Events);
    }

    [Theory]
    [InlineData("{ArgumentExtension {x:Static ProbeValues.Number}}", "int")]
    [InlineData("{ArgumentExtension {TextValue}}", "string")]
    public void NestedMarkupArgumentsKeepTheirProvidedTypes(string value, string expected)
    {
        var root = (ConstructorContainer)CompileAndRun("<ConstructorContainer" + Namespace + " Value='" + value + "'/>");
        Assert.Equal(expected, root.Value);
    }
}

public sealed class ConstructorContainer
{
    public object? Value { get; set; }
}

public class ConstructorSelection
{
    public string? Selected { get; protected set; }
}

public sealed class ObjectFirstConstructor : ConstructorSelection
{
    public ObjectFirstConstructor(object value) => Selected = "object";
    public ObjectFirstConstructor(string value) => Selected = "string";
}

public sealed class ConvertedConstructor : ConstructorSelection
{
    public ConvertedConstructor(int value) => Selected = "int";
    public ConvertedConstructor(double value) => Selected = "double";
}

public static class ConstructorOrder
{
    public static List<string> Events { get; } = new();
}

public sealed class OrderedTextValue
{
    public string ProvideValue() { ConstructorOrder.Events.Add("provide"); return "value"; }
}

public sealed class LaterArgument
{
    public LaterArgument() => ConstructorOrder.Events.Add("construct");
}

public sealed class OrderedConstructor
{
    public OrderedConstructor(string text, LaterArgument value) => ConstructorOrder.Events.Add("root");
}

public sealed class ArgumentExtension
{
    private readonly string _selected;
    public ArgumentExtension(int value) => _selected = "int";
    public ArgumentExtension(string value) => _selected = "string";
    public string ProvideValue() => _selected;
}
