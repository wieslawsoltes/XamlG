using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class TransformLiteralTests
{
    [AvaloniaTheory]
    [InlineData("none")]
    [InlineData("rotate(45deg)")]
    [InlineData("translate(10px, 20px)")]
    [InlineData("scale(2, 3) rotate(30deg)")]
    [InlineData("skew(15deg, 0deg)")]
    [InlineData("matrix(1, 0, 0, 1, 12, 34)")]
    public void AttributePreservesFrameworkOperationSemantics(string literal)
    {
        var fixture = Fixture("<Border " + ResourceProjectFixture.Namespace + " RenderTransform='" + literal + "'/>");
        var border = Assert.IsType<Border>(fixture.Build("View.axaml"));
        Equivalent(literal, border.RenderTransform);
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

    [AvaloniaFact]
    public void InvalidOperationIsNotReplacedWithIdentity()
    {
        var fixture = Fixture("<Border " + ResourceProjectFixture.Namespace + " RenderTransform='rotate(not-an-angle)'/>");
        var error = Assert.Throws<TargetInvocationException>(() => fixture.Build("View.axaml"));
        Assert.IsAssignableFrom<FormatException>(error.InnerException);
    }

    private static ResourceProjectFixture Fixture(string source) => new(new[] { ("View.axaml", source) });

    private static void Equivalent(string literal, object? value)
    {
        var expected = TransformOperations.Parse(literal);
        var actual = Assert.IsType<TransformOperations>(value);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Operations.Select(operation => operation.Type), actual.Operations.Select(operation => operation.Type));
    }
}
