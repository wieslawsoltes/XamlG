using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using XamlG.Avalonia.Tests;
using Xunit;

namespace ControlCatalog.Tests;

/// <summary>Literal APIs present in the pinned source build, beyond the packaged compatibility baseline.</summary>
public sealed class ParsedLiteralTests
{
    [AvaloniaFact]
    public void FlexBasisPreservesThePublicParserGrammarAndNumericBits()
    {
        var literals = new[] { "Auto", "aUtO", "25%", " 25% ", "1.25", "1e2", "+3", "-0", "0%", ".25%" };
        var fixture = Fixture(typeof(FlexBasis), literals);
        var values = Assert.IsType<ResourceDictionary>(fixture.Build("Parsed.axaml"));
        for (var index = 0; index < literals.Length; index++)
        {
            var expected = FlexBasis.Parse(literals[index]);
            var actual = Assert.IsType<FlexBasis>(values["v" + index]);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Value), BitConverter.DoubleToInt64Bits(actual.Value));
        }
        NoParse(fixture);
        foreach (var literal in new[] { "Auto ", "-1", "NaN", "Infinity", "1e2%", "+3%", "1,000" })
        {
            Assert.Throws<ArgumentException>(() => FlexBasis.Parse(literal));
            Assert.False(Fixture(typeof(FlexBasis), [literal]).Result.Success);
        }
    }

    [AvaloniaFact]
    public void FontVariationsPreserveAxisOrderingLastValueWinsAndEmptyIdentity()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var literals = new[] { "wght=700,wdth=85", "wdth=85,wght=700", "wght=100,wght=900", " a = -0, ab = 2.5e1 ",
                "wght=Infinity,wdth=-Infinity", ",,", "wght=1,, wdth=+2," };
            var fixture = Fixture(typeof(FontVariationSettings), literals);
            var values = Assert.IsType<ResourceDictionary>(fixture.Build("Parsed.axaml"));
            for (var index = 0; index < literals.Length; index++)
            {
                var expected = FontVariationSettings.Parse(literals[index]);
                var actual = Assert.IsType<FontVariationSettings>(values["v" + index]);
                Assert.Equal(expected, actual);
                Assert.Equal(expected.Variations.Select(value => BitConverter.DoubleToInt64Bits(value.Value)),
                    actual.Variations.Select(value => BitConverter.DoubleToInt64Bits(value.Value)));
            }
            Assert.Same(FontVariationSettings.Empty, values["v5"]);
            var output = fixture.Result.Documents.Single().Output;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == fixture.Compilation.AssemblyName);
            var second = Assert.IsType<ResourceDictionary>(assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, [null]));
            Assert.NotSame(values["v0"], second["v0"]);
            NoParse(fixture);
            foreach (var literal in new[] { "wght", "wght=", "=700", "abcde=1", "wght=NaN", "wght=1=2", "wght=1 2" })
            {
                Assert.Throws<FormatException>(() => FontVariationSettings.Parse(literal));
                Assert.False(Fixture(typeof(FontVariationSettings), [literal]).Result.Success);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static ResourceProjectFixture Fixture(Type type, string[] literals)
    {
        var xaml = "<ResourceDictionary " + ResourceProjectFixture.Namespace + " xmlns:p='clr-namespace:" + type.Namespace +
            ";assembly=" + type.Assembly.GetName().Name + "'>" + string.Concat(literals.Select((literal, index) =>
                "<p:" + type.Name + " x:Key='v" + index + "'>" + SecurityElement.Escape(literal) + "</p:" + type.Name + ">")) + "</ResourceDictionary>";
        return new([("Parsed.axaml", xaml)]);
    }

    private static void NoParse(ResourceProjectFixture fixture) =>
        Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", fixture.Result.Documents.Single().Output.Source);
}
