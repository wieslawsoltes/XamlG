using System.Globalization;
using System.Security;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class AnimationLiteralTests
{
    private static readonly string[] Cues = { "0", "100", "50%", "50%%", "1e-2%", " 25 " };
    private static readonly string[] Iterations = { "0", "1", "+12", "18446744073709551615", " infinite ", "PrefixInfinite" };
    private static readonly string[] Splines = { "0,0,1,1", "0 0, 1 1", ".5,-2,.8,3", "2,0,-1,1", "NaN,0,Infinity,1" };

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnimationResourcesMatchPublicParsersWithoutRuntimeParsing(bool sourceInfo)
    {
        var namedEasings = typeof(Easing).Assembly.GetTypes().Where(type => type.IsPublic && !type.IsAbstract &&
            type.Namespace == "Avalonia.Animation.Easings" && typeof(Easing).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
            .Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var literals = Cues.Select(text => (Type: "Cue", Text: text))
            .Concat(Iterations.Select(text => (Type: "IterationCount", Text: text)))
            .Concat(Splines.Select(text => (Type: "KeySpline", Text: text)))
            .Concat(namedEasings.Concat(Splines.Where(text => text.Contains(',', StringComparison.Ordinal)))
                .Select(text => (Type: "Easing", Text: text))).ToArray();
        var xaml = ResourceProjectFixture.Dictionary(string.Join("\n", literals.Select((literal, index) =>
            "<" + literal.Type + " x:Key='" + index + "'>" + SecurityElement.Escape(literal.Text) + "</" + literal.Type + ">")));
        var fixture = new ResourceProjectFixture(new[] { ("Animation.axaml", xaml) }, createSourceInfo: sourceInfo);
        var root = Assert.IsType<ResourceDictionary>(fixture.Build("Animation.axaml"));
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, sourceInfo, "Animation.axaml");
        Assert.Null(baseline.Error);
        var expected = Assert.IsType<ResourceDictionary>(baseline.Root);
        for (var index = 0; index < literals.Length; index++)
        {
            var literal = literals[index];
            var key = index.ToString(CultureInfo.InvariantCulture);
            var value = root[key];
            switch (literal.Type)
            {
                case "Cue": Assert.Equal(Cue.Parse(literal.Text, CultureInfo.InvariantCulture), Assert.IsType<Cue>(value)); break;
                case "IterationCount": Assert.Equal(IterationCount.Parse(literal.Text), Assert.IsType<IterationCount>(value)); break;
                case "KeySpline":
                    var spline = Assert.IsType<KeySpline>(value);
                    var parsed = KeySpline.Parse(literal.Text, CultureInfo.InvariantCulture);
                    Assert.Equal(parsed.ControlPointX1, spline.ControlPointX1);
                    Assert.Equal(parsed.ControlPointY1, spline.ControlPointY1);
                    Assert.Equal(parsed.ControlPointX2, spline.ControlPointX2);
                    Assert.Equal(parsed.ControlPointY2, spline.ControlPointY2);
                    Assert.NotSame(expected[key], value);
                    break;
                default:
                    var easing = Assert.IsAssignableFrom<Easing>(value);
                    var parsedEasing = Easing.Parse(literal.Text);
                    Assert.Equal(parsedEasing.GetType(), easing.GetType());
                    Assert.NotSame(expected[key], value);
                    if (easing is SplineEasing actualSpline)
                    {
                        var expectedSpline = Assert.IsType<SplineEasing>(parsedEasing);
                        Assert.Equal(expectedSpline.X1, actualSpline.X1);
                        Assert.Equal(expectedSpline.Y1, actualSpline.Y1);
                        Assert.Equal(expectedSpline.X2, actualSpline.X2);
                        Assert.Equal(expectedSpline.Y2, actualSpline.Y2);
                        // Parsing accepts these curves, but the public interpolation
                        // algorithm need not converge for invalid X control points.
                        if (!(expectedSpline.X1 >= 0 && expectedSpline.X1 <= 1 && expectedSpline.X2 >= 0 && expectedSpline.X2 <= 1)) break;
                    }
                    foreach (var progress in new[] { 0d, .1, .5, .9, 1d }) Assert.Equal(parsedEasing.Ease(progress), easing.Ease(progress));
                    break;
            }
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, key), SourceInfo.GetXamlSourceInfo(root, key));
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expected[key]!), SourceInfo.GetXamlSourceInfo(value!));
        }
        Assert.DoesNotMatch(@"global::Avalonia\.Animation\.(?:Easings\.)?(?:Cue|IterationCount|KeySpline|Easing)\.@?Parse\(",
            fixture.Result.Documents.Single().Output.Source);
    }

    [AvaloniaTheory]
    [InlineData("Cue", "-1%")]
    [InlineData("Cue", "101")]
    [InlineData("Cue", "NaN")]
    [InlineData("Cue", "Infinity")]
    [InlineData("Cue", "1,000")]
    [InlineData("IterationCount", "-0")]
    [InlineData("IterationCount", "18446744073709551616")]
    [InlineData("IterationCount", "1.5")]
    [InlineData("KeySpline", "0,0,1")]
    [InlineData("KeySpline", "0,0,1,1,2")]
    [InlineData("KeySpline", "0,,0,1,1")]
    [InlineData("KeySpline", "0,0,1,1,")]
    [InlineData("Easing", "linearEasing")]
    [InlineData("Easing", "0 0 1 1")]
    [InlineData("Easing", "0,0,1")]
    public void InvalidAnimationLiteralsProduceSourceDiagnostics(string type, string literal)
    {
        var xaml = ResourceProjectFixture.Dictionary("<" + type + " x:Key='value'>" + literal + "</" + type + ">");
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.NotNull(baseline.Error ?? Record.Exception(() => _ = Assert.IsType<ResourceDictionary>(baseline.Root)["value"]));
        var fixture = new ResourceProjectFixture(new[] { ("Invalid.axaml", xaml) });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3004");
    }

    [AvaloniaFact]
    public void ApplicationsCannotExtendTheFrameworksNamedEasingParser()
    {
        const string source = "namespace Avalonia.Animation.Easings { public class CustomEasing : Easing { public override double Ease(double value) => value; } }";
        var xaml = ResourceProjectFixture.Dictionary("<Easing x:Key='value'>CustomEasing</Easing>");
        var fixture = new ResourceProjectFixture(new[] { ("Custom.axaml", xaml) }, sourceCode: source);
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3004");
    }
}
