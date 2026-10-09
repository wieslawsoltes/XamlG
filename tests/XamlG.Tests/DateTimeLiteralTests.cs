using System.Globalization;
using System.Reflection;
using Xunit;

namespace XamlG.Tests;

public sealed class DateTimeLiteralTests
{
    private const string Model = "namespace Dates; public class View { public System.DateTime Value { get; set; } public System.DateTime? Nullable { get; set; } }";

    [Theory]
    [InlineData("2023-10-01")]
    [InlineData("2023-10-15")]
    [InlineData("0001-01-01")]
    [InlineData("9999-12-31T23:59:59.9999999")]
    [InlineData("2024-02-29T12:34")]
    [InlineData("2024-02-29 12:34:56.1234567")]
    [InlineData(" 2024-02-29T12:34:56 ")]
    public void CompleteZoneFreeDatesFoldWithExactTicksAndKind(string literal)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Dates' Value='" + literal + "' Nullable='" + literal + "'/>", Model);
            var root = code.Build(); var expected = DateTime.Parse(literal, CultureInfo.InvariantCulture);
            foreach (var property in new[] { "Value", "Nullable" })
            {
                var actual = Assert.IsType<DateTime>(root.GetType().GetProperty(property)!.GetValue(root));
                Assert.Equal(expected.Ticks, actual.Ticks); Assert.Equal(expected.Kind, actual.Kind);
            }
            Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", code.Emission.Source);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("12:34")]
    [InlineData("01/02")]
    [InlineData("2023-10-01T12:34:56Z")]
    [InlineData("2023-10-01T12:34:56+02:00")]
    public void CurrentDateAndTimeZoneDependentInputsKeepRuntimeParsing(string literal)
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Dates' Value='" + literal + "'/>", Model);
        Assert.Matches(@"\.\s*@?Parse\s*\(", code.Emission.Source);
        var before = DateTime.Parse(literal, CultureInfo.InvariantCulture);
        var root = code.Build();
        var actual = Assert.IsType<DateTime>(root.GetType().GetProperty("Value")!.GetValue(root));
        var after = DateTime.Parse(literal, CultureInfo.InvariantCulture);
        Assert.True(actual == before || actual == after); // Also permits a date rollover during the test.
        Assert.Equal(before.Kind, actual.Kind);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2023-02-30")]
    [InlineData("0000-01-01")]
    public void InvalidDatesKeepTheExistingRuntimeFailurePhase(string literal)
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Dates' Value='" + literal + "'/>", Model);
        Assert.Matches(@"\.\s*@?Parse\s*\(", code.Emission.Source);
        Assert.IsType<FormatException>(Assert.Throws<TargetInvocationException>(() => code.Build()).InnerException);
    }
}
