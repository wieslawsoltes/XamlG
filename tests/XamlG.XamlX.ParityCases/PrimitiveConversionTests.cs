using System;
using System.Globalization;
using XamlX;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class PrimitiveConversionTests : CompilerTestBase
{
    private const string Ns = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData(" TRUE ", true)]
    [InlineData(" False ", false)]
    [InlineData("", false)]
    public void BooleanLiteralsUseTheClrGrammar(string literal, bool expected) =>
        Assert.Equal(expected, ((PrimitiveConversionHost)CompileAndRun("<PrimitiveConversionHost" + Ns + " Boolean='" + literal + "'/>" )).Boolean);

    [Theory]
    [InlineData("Boolean", "0")]
    [InlineData("Boolean", "1")]
    [InlineData("Boolean", "yes")]
    [InlineData("Double", "INF")]
    [InlineData("Double", "-INF")]
    [InlineData("Single", "INF")]
    [InlineData("Single", "-INF")]
    public void InvalidIntrinsicLiteralsFailCompilation(string property, string literal) =>
        Assert.ThrowsAny<XamlParseException>(() => Compile("<PrimitiveConversionHost" + Ns + " " + property + "='" + literal + "'/>"));

    [Theory]
    [InlineData("1.25")]
    [InlineData("1,234.5")]
    [InlineData("-79228162514264337593543950335")]
    public void DecimalLiteralsUseInvariantRuntimeParsing(string literal)
    {
        var root = (PrimitiveConversionHost)CompileAndRun("<PrimitiveConversionHost" + Ns + " Decimal='" + literal + "'/>");
        Assert.Equal(decimal.Parse(literal, CultureInfo.InvariantCulture), root.Decimal);
    }

    [Theory]
    [InlineData("invalid", false)]
    [InlineData("invalid", true)]
    [InlineData("79228162514264337593543950336", false)]
    [InlineData("79228162514264337593543950336", true)]
    public void InvalidDecimalsFailWhenTheFactoryRuns(string literal, bool element)
    {
        var xaml = element ? "<PrimitiveConversionHost" + Ns + "><PrimitiveConversionHost.Decimal><x:String>" + literal + "</x:String></PrimitiveConversionHost.Decimal></PrimitiveConversionHost>" :
            "<PrimitiveConversionHost" + Ns + " Decimal='" + literal + "'/>";
        var factory = Compile(xaml).create!;
        var error = Record.Exception(() => factory(null));
        Assert.NotNull(error);
        Assert.Equal(literal == "invalid" ? typeof(FormatException) : typeof(OverflowException), error.GetBaseException().GetType());
    }
}

public sealed class PrimitiveConversionHost
{
    public bool Boolean { get; set; }
    public float Single { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
}
