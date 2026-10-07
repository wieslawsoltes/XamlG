using System;
using System.Collections.Generic;
using System.ComponentModel;
using XamlX;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class StringCollectionConversionTests : CompilerTestBase
{
    private const string Ns = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("attribute")]
    [InlineData("static-attribute")]
    [InlineData("text")]
    [InlineData("string")]
    [InlineData("static")]
    [InlineData("provider")]
    public void ItemConversionPrecedesAnAssignableStringCollection(string form)
    {
        var root = (StringCollectionConversionContainer)CompileAndRun(Document(form, "a"));
        Assert.Equal(0, root.SetterCalls);
        Assert.Equal(new[] { 'a' }, root.Characters);
    }

    [Theory]
    [InlineData("attribute")]
    [InlineData("static-attribute")]
    [InlineData("text")]
    [InlineData("string")]
    [InlineData("static")]
    [InlineData("provider")]
    public void InvalidItemConversionDoesNotFallBackToStringAssignment(string form)
    {
        var xaml = Document(form, "ab");
        if (form is "static-attribute" or "static" or "provider")
            Assert.Throws<FormatException>(() => CompileAndRun(xaml));
        else
            Assert.ThrowsAny<XamlParseException>(() => Compile(xaml));
    }

    private static string Document(string form, string value)
    {
        var member = value == "a" ? "ShortValue" : "LongValue";
        var expression = "{x:Static StringCollectionConversionContainer." + member + "}";
        if (form is "attribute" or "static-attribute")
            return "<StringCollectionConversionContainer" + Ns + " Characters='" + (form == "attribute" ? value : expression) + "'/>";
        var content = form switch
        {
            "string" => "<x:String>" + value + "</x:String>",
            "static" => "<x:Static Member='StringCollectionConversionContainer." + member + "'/>",
            "provider" => "<CollectionStringProvider Text='" + value + "'/>",
            _ => value
        };
        return "<StringCollectionConversionContainer" + Ns + "><StringCollectionConversionContainer.Characters>" + content +
            "</StringCollectionConversionContainer.Characters></StringCollectionConversionContainer>";
    }
}

public sealed class StringCollectionConversionContainer
{
    private IEnumerable<char> _characters = new List<char>();
    public static string ShortValue => "a";
    public static string LongValue => "ab";
    public int SetterCalls { get; private set; }
    [TypeConverter(typeof(RejectIdentityConverter))]
    public IEnumerable<char> Characters { get => _characters; set { SetterCalls++; _characters = value; } }
}

public sealed class CollectionStringProvider
{
    public string Text { get; set; } = "";
    public string ProvideValue() => Text;
}
