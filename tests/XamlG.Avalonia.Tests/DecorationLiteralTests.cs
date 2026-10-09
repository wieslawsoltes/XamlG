using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using System.Reflection;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DecorationLiteralTests
{
    [AvaloniaTheory]
    [InlineData("Underline,Strikethrough")]
    [InlineData("overline,baseline")]
    [InlineData("0,1,2,3")]
    [InlineData("4,-1")]
    [InlineData(" Underline , Baseline ")]
    public void DecorationFallbackPreservesLocationsAndFreshMutableItems(string literal)
    {
        var xaml = "<TextBlock " + ResourceProjectFixture.Namespace + " TextDecorations='" + literal + "'/>";
        var fixture = new ResourceProjectFixture([("Decorations.axaml", xaml)]);
        var actual = Assert.IsType<TextBlock>(fixture.Build("Decorations.axaml")).TextDecorations!;
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        var expected = Assert.IsType<TextBlock>(baseline.Root).TextDecorations!;
        Assert.Equal(TextDecorationCollection.Parse(literal).Select(item => item.Location), actual.Select(item => item.Location));
        Assert.Equal(expected.Select(item => item.Location), actual.Select(item => item.Location));
        var output = fixture.Result.Documents.Single().Output;
        Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", output.Source);
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == fixture.Compilation.AssemblyName);
        var other = Assert.IsType<TextBlock>(assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, [null])).TextDecorations!;
        Assert.NotSame(actual, other); Assert.NotSame(actual[0], other[0]);
        actual[0].StrokeThickness = 9;
        Assert.Equal(1, other[0].StrokeThickness);
    }

    [AvaloniaFact]
    public void SingleIntrinsicDecorationRetainsSharedIdentity()
    {
        var fixture = new ResourceProjectFixture([("Single.axaml", "<TextBlock " + ResourceProjectFixture.Namespace + " TextDecorations='uNdErLiNe'/>")]);
        Assert.Same(TextDecorations.Underline, Assert.IsType<TextBlock>(fixture.Build("Single.axaml")).TextDecorations);
    }

    [AvaloniaTheory]
    [InlineData("Underline,underline")]
    [InlineData("0,Underline")]
    [InlineData("Underline,,Overline")]
    [InlineData("missing")]
    public void InvalidDecorationsKeepTheirRuntimeFailurePhase(string literal)
    {
        var expected = Record.Exception(() => TextDecorationCollection.Parse(literal));
        Assert.NotNull(expected);
        var fixture = new ResourceProjectFixture([("Invalid.axaml", "<TextBlock " + ResourceProjectFixture.Namespace + " TextDecorations='" + literal + "'/>")]);
        Assert.True(fixture.Result.Success);
        Assert.Matches(@"\.\s*@?Parse\s*\(", fixture.Result.Documents.Single().Output.Source);
        var actual = Assert.Throws<TargetInvocationException>(() => fixture.Build("Invalid.axaml"));
        Assert.Equal(expected.GetType(), actual.InnerException!.GetType());
    }
}
