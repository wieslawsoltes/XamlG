using System.Security;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using XamlG.Runtime;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class TransformLiteralTests
{
    [AvaloniaTheory]
    [InlineData("none")]
    [InlineData("rotate(45deg)")]
    [InlineData("translate(10px, 20px)")]
    [InlineData("scale(2, 3) rotate(30deg)")]
    [InlineData("skew(15deg, 0deg)")]
    [InlineData("matrix(1,0,0,1,12,34)")]
    [InlineData("translateX(-2.5px) translateY(3px) scaleX(2) scaleY(-3)")]
    [InlineData("skewX(0.1rad) skewY(-.25turn) rotate(100grad)")]
    [InlineData("  NoNe  ")]
    [InlineData("TrAnSlAtE(0) rotate(0px) skew(0,0) scale(1)")]
    [InlineData("translate(-0px,-0px) scale(-0,-0) rotate(-0deg)")]
    public void AttributePreservesFrameworkOperationSemantics(string literal)
    {
        var fixture = Fixture("<Border " + ResourceProjectFixture.Namespace + " RenderTransform='" + literal + "'/>");
        var border = Assert.IsType<Border>(fixture.Build("View.axaml"));
        Equivalent(literal, border.RenderTransform);
        Assert.DoesNotContain(".Parse(", fixture.Result.Documents.Single().Output.Source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void PropertyElementUsesTheSameOperationParser()
    {
        var fixture = Fixture("<Border " + ResourceProjectFixture.Namespace +
            "><Border.RenderTransform>scale(1.5) rotate(30deg)</Border.RenderTransform></Border>");
        Equivalent("scale(1.5) rotate(30deg)", Assert.IsType<Border>(fixture.Build("View.axaml")).RenderTransform);
    }

    [AvaloniaFact]
    public void StyleSetterProducesOperationsRatherThanAMatrixTransform()
    {
        var fixture = Fixture("<Style " + ResourceProjectFixture.Namespace +
            " Selector='Border'><Setter Property='RenderTransform' Value='rotate(45deg)'/></Style>");
        var style = Assert.IsType<Style>(fixture.Build("View.axaml"));
        Equivalent("rotate(45deg)", Assert.IsType<Setter>(Assert.Single(style.Setters)).Value);
    }

    [AvaloniaFact]
    public void ConcreteMatrixLiteralsRetainTheirOriginalContract()
    {
        var fixture = Fixture("<MatrixTransform " + ResourceProjectFixture.Namespace + " Matrix='1,0,0,1,12,34'/>");
        Assert.Equal(new Matrix(1, 0, 0, 1, 12, 34), Assert.IsType<MatrixTransform>(fixture.Build("View.axaml")).Matrix);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyAttributesRetainXamlWhitespaceHandling(string literal)
    {
        var xaml = "<Border " + ResourceProjectFixture.Namespace + " RenderTransform='" + literal + "'/>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        Assert.Null(Assert.IsType<Border>(baseline.Root).RenderTransform);
        Assert.Null(Assert.IsType<Border>(Fixture(xaml).Build("View.axaml")).RenderTransform);
    }

    [AvaloniaTheory]
    [InlineData("rotate(not-an-angle)")]
    [InlineData("matrix(1, 0, 0, 1, 12, 34)")]
    [InlineData("translate(1)")]
    [InlineData("rotate(1)")]
    [InlineData("scale(1px)")]
    [InlineData("rotate(1e2deg)")]
    [InlineData("rotate(+1deg)")]
    [InlineData("scaleX(1,2)")]
    [InlineData("matrix(1,0,0,1,0)")]
    [InlineData("matrix(1,0,0,1,0,0,0)")]
    [InlineData("matrix(1,0,0,1,0,0,)")]
    [InlineData("rotate(1deg) trailing")]
    public void RejectedOperationsProduceSourceDiagnosticsWithThePinnedParserFailure(string literal)
    {
        // The pinned upstream matrix parser does not trim its final comma-delimited
        // value. Preserve that observable contract, including its diagnostic, rather
        // than silently normalizing invalid input or returning an identity transform.
        var expected = Assert.Throws<FormatException>(() => TransformOperations.Parse(literal));
        var fixture = Fixture("<Border " + ResourceProjectFixture.Namespace + " RenderTransform='" + literal + "'/>");
        Assert.False(fixture.Result.Success);
        var error = Assert.Single(fixture.Result.Documents.Single().Output.Diagnostics.Where(diagnostic => diagnostic.Code == "XG3004"));
        Assert.Contains(expected.Message, error.Message, StringComparison.Ordinal);
        Assert.True(error.Span.Length > 0);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceLiteralsPreserveOperationsInterpolationAndSourceLocations(bool sourceInfo)
    {
        var literals = new[] { "none", "scale(2,3) rotate(30deg)", "scale(4,5) rotate(60deg)",
            "translate(2px,-3px) skew(15deg,20grad)", "matrix(1,0,0,1,12,34)",
            "rotate(1rad) scale(0)", "rotate(-0deg) translate(-0px,-0px)", "rotate(.1turn)", "scale(1)" };
        var xaml = ResourceProjectFixture.Dictionary(string.Concat(literals.Select((literal, index) =>
            "<TransformOperations x:Key='v" + index + "'>" + SecurityElement.Escape(literal) + "</TransformOperations>")));
        var fixture = new ResourceProjectFixture(new[] { ("Transforms.axaml", xaml) }, createSourceInfo: sourceInfo);
        var actual = Assert.IsType<ResourceDictionary>(fixture.Build("Transforms.axaml"));
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, sourceInfo, "Transforms.axaml");
        Assert.Null(baseline.Error);
        var expected = Assert.IsType<ResourceDictionary>(baseline.Root);
        for (var index = 0; index < literals.Length; index++)
        {
            var key = "v" + index;
            Equivalent(literals[index], actual[key]);
            Equal(Assert.IsType<TransformOperations>(expected[key]), Assert.IsType<TransformOperations>(actual[key]));
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, key), SourceInfo.GetXamlSourceInfo(actual, key));
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expected[key]!), SourceInfo.GetXamlSourceInfo(actual[key]!));
            foreach (var progress in new[] { 0d, .25, .5, .75, 1d })
                Equal(TransformOperations.Interpolate(TransformOperations.Parse(literals[index]), TransformOperations.Parse(literals[(index + 1) % literals.Length]), progress),
                    TransformOperations.Interpolate((TransformOperations)actual[key]!, (TransformOperations)actual["v" + ((index + 1) % literals.Length)]!, progress));
        }
        var output = fixture.Result.Documents.Single().Output;
        Assert.DoesNotContain(".Parse(", output.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("XamlG.Frameworks.Avalonia.Parsing", output.Source, StringComparison.Ordinal);
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == fixture.Compilation.AssemblyName);
        var second = Assert.IsType<ResourceDictionary>(assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, new object?[] { null }));
        Assert.Same(TransformOperations.Identity, actual["v0"]);
        Assert.NotSame(actual["v1"], second["v1"]);
        Assert.True(XamlRuntimeSession.TryGet(actual, out var session)); session!.Dispose();
        Assert.True(XamlRuntimeSession.TryGet(second, out session)); session!.Dispose();
    }

    private static ResourceProjectFixture Fixture(string source) => new(new[] { ("View.axaml", source) });

    private static void Equivalent(string literal, object? value)
    {
        var expected = TransformOperations.Parse(literal);
        var actual = Assert.IsType<TransformOperations>(value);
        Equal(expected, actual);
    }

    private static void Equal(TransformOperations expected, TransformOperations actual)
    {
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.IsIdentity, actual.IsIdentity);
        Assert.Equal(expected.Operations.Select(operation => operation.Type), actual.Operations.Select(operation => operation.Type));
        Assert.Equal(expected.Operations.Select(operation => operation.Matrix), actual.Operations.Select(operation => operation.Matrix));
    }
}
