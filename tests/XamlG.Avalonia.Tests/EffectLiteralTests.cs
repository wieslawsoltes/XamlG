using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class EffectLiteralTests
{
    [AvaloniaTheory]
    [InlineData("blur(2.5)")]
    [InlineData(" blur ( -0 ) ")]
    [InlineData("blur(-2)")]
    [InlineData("drop-shadow(1 2)")]
    [InlineData("drop-shadow(-1 2 3)")]
    [InlineData("drop-shadow(1 2 3 #123456)")]
    [InlineData("drop-shadow(-0 -0 -0 rgba(1,2,3,.5))")]
    [InlineData("drop-shadow(1 2 3 hsl(180,50%,50%))")]
    public void EffectsPreserveTheConverterGrammarAndFreshValues(string literal)
    {
        var xaml = "<Border " + ResourceProjectFixture.Namespace + " Effect='" + literal + "'/>";
        var fixture = new ResourceProjectFixture([("Effect.axaml", xaml)]);
        var actual = Assert.IsType<Border>(fixture.Build("Effect.axaml")).Effect;
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        Equal(Effect.Parse(literal), actual!);
        Equal(Assert.IsType<Border>(baseline.Root).Effect!, actual!);
        var output = fixture.Result.Documents.Single().Output;
        Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", output.Source);
        Assert.DoesNotContain("EffectConverter", output.Source, StringComparison.Ordinal);
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == fixture.Compilation.AssemblyName);
        var second = Assert.IsType<Border>(assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, [null]));
        Assert.NotSame(actual, second.Effect);
    }

    [AvaloniaTheory]
    [InlineData("Blur(1)")]
    [InlineData("blur(1e2)")]
    [InlineData("blur(+1)")]
    [InlineData("blur(1) junk")]
    [InlineData("drop-shadow(1,2)")]
    [InlineData("drop-shadow(1 2 -3)")]
    [InlineData("drop-shadow(1 2 red)")]
    [InlineData("drop-shadow(1 2 3 invalid)")]
    public void RejectedEffectsProduceSourceDiagnostics(string literal)
    {
        Assert.Throws<ArgumentException>(() => Effect.Parse(literal));
        var fixture = new ResourceProjectFixture([("Invalid.axaml", "<Border " + ResourceProjectFixture.Namespace + " Effect='" + literal + "'/>")]);
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, error => error.Code == "XG3004");
    }

    [AvaloniaFact]
    public void CacheModesUseFreshTypedInstancesAndExactCase()
    {
        var xaml = "<StackPanel " + ResourceProjectFixture.Namespace + "><Border CacheMode='BitmapCache'/><Border CacheMode='BitmapCache'/></StackPanel>";
        var fixture = new ResourceProjectFixture([("Cache.axaml", xaml)]);
        var panel = Assert.IsType<StackPanel>(fixture.Build("Cache.axaml"));
        var first = Assert.IsType<BitmapCache>(((Border)panel.Children[0]).CacheMode);
        var second = Assert.IsType<BitmapCache>(((Border)panel.Children[1]).CacheMode);
        Assert.NotSame(first, second);
        Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", fixture.Result.Documents.Single().Output.Source);
        foreach (var literal in new[] { "bitmapcache", "BitmapCache ", "Other" })
        {
            Assert.Throws<ArgumentException>(() => CacheMode.Parse(literal));
            var invalid = new ResourceProjectFixture([("Invalid.axaml", "<Border " + ResourceProjectFixture.Namespace + " CacheMode='" + literal + "'/>")]);
            Assert.False(invalid.Result.Success);
        }
    }

    private static void Equal(IEffect expected, IEffect actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        foreach (var property in expected.GetType().GetProperties())
        {
            var left = property.GetValue(expected); var right = property.GetValue(actual);
            if (left is double number) Assert.Equal(BitConverter.DoubleToInt64Bits(number), BitConverter.DoubleToInt64Bits((double)right!));
            else Assert.Equal(left, right);
        }
    }
}
