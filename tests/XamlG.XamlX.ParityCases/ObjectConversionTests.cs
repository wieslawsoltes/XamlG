using System.ComponentModel;
using System.Globalization;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class ObjectConversionTests : CompilerTestBase
{
    private const string Namespace = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("ParsedReference", "parse:hello")]
    [InlineData("ConvertedReference", "converter:hello")]
    public void ReferenceObjectsWithTextUseTheirConversion(string type, string expected)
    {
        var root = (ConstructorContainer)CompileAndRun("<ConstructorContainer" + Namespace + "><ConstructorContainer.Value><" + type + ">hello</" + type + "></ConstructorContainer.Value></ConstructorContainer>");
        Assert.Equal(expected, ((ConvertedReference)root.Value!).Text);
    }

}

[TypeConverter(typeof(ReferenceConverter))]
public class ConvertedReference
{
    protected ConvertedReference(string text) => Text = text;
    public string Text { get; }
    public static ConvertedReference FromConverter(string text) => new("converter:" + text);
}

public sealed class ParsedReference : ConvertedReference
{
    private ParsedReference(string text) : base(text) { }
    public static ParsedReference Parse(string text) => new("parse:" + text);
}

public sealed class ReferenceConverter : TypeConverter
{
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => ConvertedReference.FromConverter((string)value);
}
