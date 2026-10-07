using System;
using System.ComponentModel;
using System.Globalization;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class StringIdentityConversionTests : CompilerTestBase
{
    private const string Ns = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("Text", "literal")]
    [InlineData("Boxed", "literal")]
    [InlineData("Comparable", "literal")]
    [InlineData("Text", "element")]
    [InlineData("Boxed", "element")]
    [InlineData("Comparable", "element")]
    [InlineData("Text", "static")]
    [InlineData("Boxed", "static")]
    [InlineData("Comparable", "static")]
    public void AssignableStringsDoNotRequireMemberConversion(string property, string form)
    {
        var xaml = form == "element"
            ? "<StringIdentityContainer" + Ns + "><StringIdentityContainer." + property + "><x:String>provided</x:String></StringIdentityContainer." + property + "></StringIdentityContainer>"
            : "<StringIdentityContainer" + Ns + " " + property + "='" + (form == "static" ? "{x:Static StringIdentityContainer.Value}" : "provided") + "'/>";
        var root = (StringIdentityContainer)CompileAndRun(xaml);
        var value = property switch { "Text" => root.Text, "Boxed" => root.Boxed, _ => root.Comparable };
        Assert.Equal("provided", Assert.IsType<string>(value));
    }
}

public sealed class StringIdentityContainer
{
    public static string Value => "provided";
    [TypeConverter(typeof(RejectIdentityConverter))] public string? Text { get; set; }
    [TypeConverter(typeof(RejectIdentityConverter))] public object? Boxed { get; set; }
    [TypeConverter(typeof(RejectIdentityConverter))] public IComparable? Comparable { get; set; }
}

public sealed class RejectIdentityConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => throw new InvalidOperationException("An assignable string must not invoke a converter.");
}
