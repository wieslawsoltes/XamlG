using System.Globalization;
using System.Security;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class InitializedLiteralTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FontFeaturesPreserveRangesOverflowAndInvalidSyntaxDefaults(bool sourceInfo)
    {
        var literals = new[] { "kern", "+kern", "-kern", "kern=0", "kern=on", "kern=off", "aalt=2",
            "kern[]", "kern[:]", "kern[5:]", "kern[:5]", "kern[3:5]", "kern[3]", "aalt[3:5]=2",
            " kern [ 3 : 5 ] = 2 ", "abcd[2147483647]", "abcd[2147483648]", "abcd[:2147483648]",
            "abcd=2147483648", "abcd[5:3]", "____", "éééé", "1234", "-kern=2", "kern=OFF", "bad", "kern[-1]" };
        var (fixture, actual, expected) = Compile("FontFeature", literals, sourceInfo);
        var output = fixture.Result.Documents.Single().Output;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == fixture.Compilation.AssemblyName);
        var second = Assert.IsType<ResourceDictionary>(assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, new object?[] { null }));
        for (var index = 0; index < literals.Length; index++)
        {
            var key = index.ToString(CultureInfo.InvariantCulture);
            var value = Assert.IsType<FontFeature>(actual[key]);
            Assert.Equal(FontFeature.Parse(literals[index]), value);
            Assert.Equal(expected[key], value);
            Assert.NotSame(value, second[key]);
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, key), SourceInfo.GetXamlSourceInfo(actual, key));
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expected[key]!), SourceInfo.GetXamlSourceInfo(value));
        }
        Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", fixture.Result.Documents.Single().Output.Source);
        Assert.Contains("@Tag =", fixture.Result.Documents.Single().Output.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("UnsafeAccessor", fixture.Result.Documents.Single().Output.Source, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShadowsPreserveBracketGrammarNumbersAndEmptyEntries(bool sourceInfo)
    {
        var literals = new[] { "none", "  none ", "1 2 red", "inset 1 2 3 #112233", "1 2 3 4 rgba(50%, 20%, 10%, .5)",
            "-0 -0 -0 -0 #00000000", "NaN Infinity -Infinity red", "1 2 3 4 red ignored", "1\t2\tblue",
            "1e2 -2.5 -3 -4 hsl(180,50%,50%)", "0 0 #00000000" };
        var (fixture, actual, expected) = Compile("BoxShadow", literals, sourceInfo);
        for (var index = 0; index < literals.Length; index++)
        {
            var key = index.ToString(CultureInfo.InvariantCulture);
            var value = Assert.IsType<BoxShadow>(actual[key]);
            Equal(BoxShadow.Parse(literals[index]), value);
            Equal(Assert.IsType<BoxShadow>(expected[key]), value);
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, key), SourceInfo.GetXamlSourceInfo(actual, key));
        }
        Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", fixture.Result.Documents.Single().Output.Source);
        var lists = literals.Concat(new[] { ",", " ,", "none,none", "0 0 #00000000,none", "1 2 rgba(0,0,0,.5), inset 3 4 blue", ",none,," }).ToArray();
        var (listFixture, actualLists, expectedLists) = Compile("BoxShadows", lists, sourceInfo);
        for (var index = 0; index < lists.Length; index++)
        {
            var key = index.ToString(CultureInfo.InvariantCulture);
            var value = Assert.IsType<BoxShadows>(actualLists[key]);
            var parsed = BoxShadows.Parse(lists[index]);
            var baseline = Assert.IsType<BoxShadows>(expectedLists[key]);
            Assert.Equal(parsed.Count, value.Count);
            Assert.Equal(baseline.Count, value.Count);
            for (var item = 0; item < value.Count; item++) { Equal(parsed[item], value[item]); Equal(baseline[item], value[item]); }
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expectedLists, key), SourceInfo.GetXamlSourceInfo(actualLists, key));
        }
        Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", listFixture.Result.Documents.Single().Output.Source);
    }

    [AvaloniaTheory]
    [InlineData("1 2 rgba(1,2,3,.5")]
    [InlineData("1 2 red)")]
    [InlineData("1 2")]
    [InlineData("None")]
    [InlineData("INSET 1 2 red")]
    [InlineData("1 2 3 4 red extra extra")]
    [InlineData("1 2 notacolor")]
    [InlineData("1 2 rgb((1),2,3)")]
    public void MalformedShadowsProduceSourceDiagnostics(string literal)
    {
        foreach (var type in new[] { "BoxShadow", "BoxShadows" })
        {
            var xaml = ResourceProjectFixture.Dictionary("<" + type + " x:Key='value'>" + literal + "</" + type + ">");
            var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
            Assert.NotNull(baseline.Error ?? Record.Exception(() => _ = Assert.IsType<ResourceDictionary>(baseline.Root)["value"]));
            var fixture = new ResourceProjectFixture(new[] { ("Invalid.axaml", xaml) });
            Assert.False(fixture.Result.Success);
            Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3004");
        }
    }

    private static void Equal(BoxShadow expected, BoxShadow actual)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.OffsetX), BitConverter.DoubleToInt64Bits(actual.OffsetX));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.OffsetY), BitConverter.DoubleToInt64Bits(actual.OffsetY));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Blur), BitConverter.DoubleToInt64Bits(actual.Blur));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Spread), BitConverter.DoubleToInt64Bits(actual.Spread));
        Assert.Equal(expected.Color, actual.Color); Assert.Equal(expected.IsInset, actual.IsInset);
    }

    private static (ResourceProjectFixture Fixture, ResourceDictionary Actual, ResourceDictionary Expected) Compile(string type, string[] literals, bool sourceInfo)
    {
        var xaml = ResourceProjectFixture.Dictionary(string.Join("\n", literals.Select((literal, index) =>
            "<" + type + " x:Key='" + index.ToString(CultureInfo.InvariantCulture) + "'>" + SecurityElement.Escape(literal) + "</" + type + ">")));
        var fixture = new ResourceProjectFixture(new[] { ("Initialized.axaml", xaml) }, createSourceInfo: sourceInfo);
        var actual = Assert.IsType<ResourceDictionary>(fixture.Build("Initialized.axaml"));
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, sourceInfo, "Initialized.axaml");
        Assert.Null(baseline.Error);
        return (fixture, actual, Assert.IsType<ResourceDictionary>(baseline.Root));
    }
}
