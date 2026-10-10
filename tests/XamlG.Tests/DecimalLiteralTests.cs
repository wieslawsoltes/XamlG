using System.Globalization;
using System.Reflection;
using Xunit;

namespace XamlG.Tests;

public sealed class DecimalLiteralTests
{
    private const string Model = """
        namespace Decimals;
        public class View {
            public decimal Value { get; set; }
            public decimal? Nullable { get; set; }
        }
        """;
    private const string Ns = "xmlns='clr-namespace:Decimals' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("-0.00")]
    [InlineData("-0.0000000000000000000000000000")]
    [InlineData("0.0000")]
    [InlineData("1.2300")]
    [InlineData(" 1,234.500 ")]
    [InlineData("123.00-")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("1.23456789012345678901234567895")]
    public void ValidLiteralsPreserveAllDecimalBitsWithoutRuntimeParsing(string literal)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            using var code = CompiledXaml.Create("<View " + Ns + " Value='" + literal + "' Nullable='" + literal + "'/>", Model);
            var root = code.Build();
            var expected = decimal.GetBits(decimal.Parse(literal, CultureInfo.InvariantCulture));
            foreach (var name in new[] { "Value", "Nullable" })
                Assert.Equal(expected, decimal.GetBits(Assert.IsType<decimal>(root.GetType().GetProperty(name)!.GetValue(root))));
            Assert.DoesNotContain(".@Parse(", code.Emission.Source, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("1e2")]
    [InlineData("79228162514264337593543950336")]
    public void InvalidLiteralsKeepTheirRuntimeParserFailure(string literal)
    {
        using var code = CompiledXaml.Create("<View " + Ns + " Value='" + literal + "'/>", Model);
        var expected = Record.Exception(() => decimal.Parse(literal, CultureInfo.InvariantCulture));
        var actual = Assert.Throws<TargetInvocationException>(() => code.Build()).GetBaseException();
        Assert.Equal(expected!.GetType(), actual.GetType());
        Assert.Contains(".@Parse(", code.Emission.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TextInitializedDecimalKeepsItsExplicitStringRuntimeConversion()
    {
        using var code = CompiledXaml.Create("<View " + Ns + " xmlns:s='clr-namespace:System;assembly=System.Private.CoreLib'>" +
            "<View.Value><s:Decimal><x:String>1.2300</x:String></s:Decimal></View.Value></View>", Model);
        var root = code.Build();
        Assert.Equal(decimal.GetBits(1.2300M), decimal.GetBits(Assert.IsType<decimal>(root.GetType().GetProperty("Value")!.GetValue(root))));
        Assert.Contains(".@Parse(", code.Emission.Source, StringComparison.Ordinal);
    }
}
