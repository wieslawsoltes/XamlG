using System;
using System.ComponentModel;
using System.Globalization;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class StringValueConversionTests : CompilerTestBase
{
    private const string Namespace = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData(" Number='{x:Static RuntimeText.Number}'", "")]
    [InlineData(" Number='{TextValue}'", "")]
    [InlineData("", "<ConversionRoot.Number><TextValue/></ConversionRoot.Number>")]
    [InlineData("", "<ConversionRoot.Number><x:Static Member='RuntimeText.Number'/></ConversionRoot.Number>")]
    public void RuntimeStringsUseParseForScalarValues(string attributes, string content)
    {
        var root = (ConversionRoot)CompileAndRun("<ConversionRoot" + Namespace + attributes + ">" + content + "</ConversionRoot>");
        Assert.Equal(42, root.Number);
    }

    [Fact]
    public void RuntimeStringsUseParameterConversions()
    {
        var root = (ConversionRoot)CompileAndRun("<ConversionRoot" + Namespace + "><ConversionRoot.Argument><NumberArgument><x:Arguments><TextValue/></x:Arguments></NumberArgument></ConversionRoot.Argument></ConversionRoot>");
        Assert.Equal(42, root.Argument!.Value);
    }

    [Fact]
    public void RuntimeStringConversionPrecedesAnEarlierStringAdder()
    {
        var root = (NumberConversionCollection)CompileAndRun("<NumberConversionCollection" + Namespace + "><TextValue/></NumberConversionCollection>");
        Assert.Equal("int:42", root.Selected);
    }

    [Fact]
    public void RuntimeStringsCanConvertACollectionReplacement()
    {
        var root = (ConvertibleCollectionRoot)CompileAndRun("<ConvertibleCollectionRoot" + Namespace + "><ConvertibleCollectionRoot.Items><TextValue/></ConvertibleCollectionRoot.Items></ConvertibleCollectionRoot>");
        Assert.Equal("parsed:42", root.Items.Selected);
    }

    [Theory]
    [InlineData("{TextValue}")]
    [InlineData("{x:Static RuntimeText.Number}")]
    public void RuntimeStringsHonorMemberConverters(string value)
    {
        var root = (ConversionRoot)CompileAndRun("<ConversionRoot" + Namespace + " Converted='" + value + "'/>");
        Assert.Equal("converted:42", root.Converted!.Text);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("{TextValue}")]
    public void AttributeConversionsCanPopulateReadOnlyCollections(string value)
    {
        var root = (ConversionRoot)CompileAndRun("<ConversionRoot" + Namespace + " Numbers='" + value + "'/>");
        Assert.Equal(42, root.Numbers.Value);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("{TextValue}")]
    public void AttributeValuesRequireConversionForReadOnlyCollections(string value)
    {
        Assert.Throws<XamlX.XamlLoadException>(() => Compile("<ConversionRoot" + Namespace + " Strings='" + value + "'/>"));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("{TextValue}")]
    public void ParseCallsPreserveTheSelectedCultureOverload(string value)
    {
        var root = (ConversionRoot)CompileAndRun("<ConversionRoot" + Namespace + " Parsed='" + value + "'/>");
        Assert.Equal("provider", root.Parsed!.Selected);
    }
}

public static class RuntimeText
{
    public static string Number => "42";
}

public sealed class TextValue
{
    public string ProvideValue() => "42";
}

public sealed class ConversionRoot
{
    public int Number { get; set; }
    public NumberArgument? Argument { get; set; }
    public NumberCollection Numbers { get; } = new();
    public TypedCollection Strings { get; } = new();
    public CultureParseValue? Parsed { get; set; }
    [TypeConverter(typeof(MemberValueConverter))]
    public ParsedMemberValue? Converted { get; set; }
}

public sealed class NumberArgument(int value)
{
    public int Value { get; } = value;
}

public sealed class ParsedMemberValue
{
    public string? Text { get; init; }
    public static ParsedMemberValue Parse(string value) => new() { Text = "parsed:" + value };
}

public sealed class MemberValueConverter : TypeConverter
{
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => new ParsedMemberValue { Text = "converted:" + value };
}

public sealed class CultureParseValue
{
    public string? Selected { get; init; }
    public static CultureParseValue Parse(string value, IFormatProvider culture) => new() { Selected = "provider" };
    public static CultureParseValue Parse(string value, CultureInfo culture) => new() { Selected = "culture" };
}
