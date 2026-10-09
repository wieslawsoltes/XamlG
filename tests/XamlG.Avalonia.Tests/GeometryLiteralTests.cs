using System.Globalization;
using System.Reflection;
using System.Security;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class GeometryLiteralTests
{
    private static readonly string[] Paths =
    [
        "M0,0 L10,0 10,10 0,10z", "F1 m2,3 h10 v10 h-10z m3,3 h4 v4 h-4z",
        "M1,2 3,4 5,6", "M1,2 m3,4 l5,6z l2,3", "L10,20 H30 V40",
        "M0,0 Q10,20 20,0 T40,0 t20,0", "M0,0 C10,20 20,20 30,0 S50,-20 60,0 s20,20 30,0",
        "M0,0 q10,20 20,0 t20,0 c10,20 20,20 30,0 s20,-20 30,0",
        "M0,0 Q1,2 3,4 L5,6 T7,8 C1,2 3,4 5,6 H10 S7,8 9,10",
        "M0,0 A10,20 45 0 1 30,40 a5,10 -30 1 0 -10,-20z",
        "M-0,-0 L1e+2,-1E-2 .5,-.25", "F0 M0,0L10,0L10,10z F1 M20,20L30,20L30,30z",
        "M0,0 M1,1", "z", "F1", " M1\t2\nL3,4  "
    ];

    [AvaloniaTheory]
    [InlineData("Geometry")]
    [InlineData("StreamGeometry")]
    [InlineData("PathGeometry")]
    [InlineData("PathFigures")]
    public void GeometryCallsPreserveAllCommandFamiliesFiguresMetadataAndFreshness(string type)
    {
        using var platform = SkiaScope();
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            foreach (var sourceInfo in new[] { false, true })
            {
                var xaml = ResourceProjectFixture.Dictionary(string.Concat(Paths.Select((path, index) =>
                    "<" + type + " x:Key='v" + index + "'>" + SecurityElement.Escape(path) + "</" + type + ">")));
                var fixture = new ResourceProjectFixture([("Geometry.axaml", xaml)], createSourceInfo: sourceInfo);
                var actual = Assert.IsType<ResourceDictionary>(fixture.Build("Geometry.axaml"));
                var baseline = AvaloniaUpstreamCompilation.Compile(xaml, sourceInfo, "Geometry.axaml");
                Assert.Null(baseline.Error);
                var expected = Assert.IsType<ResourceDictionary>(baseline.Root);
                for (var index = 0; index < Paths.Length; index++)
                {
                    var key = "v" + index;
                    if (type == "PathFigures")
                    {
                        Figures(PathFigures.Parse(Paths[index]), Assert.IsType<PathFigures>(actual[key]));
                        Figures(Assert.IsType<PathFigures>(expected[key]), (PathFigures)actual[key]!);
                    }
                    else
                    {
                        Equal(Geometry.Parse(Paths[index]), Assert.IsAssignableFrom<Geometry>(actual[key]));
                        Equal(Assert.IsAssignableFrom<Geometry>(expected[key]), (Geometry)actual[key]!);
                    }
                    Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, key), SourceInfo.GetXamlSourceInfo(actual, key));
                    Assert.Equal(SourceInfo.GetXamlSourceInfo(expected[key]!), SourceInfo.GetXamlSourceInfo(actual[key]!));
                }
                var output = fixture.Result.Documents.Single().Output;
                Assert.DoesNotContain(".Parse(", output.Source, StringComparison.Ordinal);
                Assert.Contains("using (var", output.Source, StringComparison.Ordinal);
                Assert.DoesNotContain("XamlG.Frameworks.Avalonia.Parsing", output.Source, StringComparison.Ordinal);
                var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == fixture.Compilation.AssemblyName);
                var second = Assert.IsType<ResourceDictionary>(assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, [null]));
                Assert.NotSame(actual["v0"], second["v0"]);
            }
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [AvaloniaFact]
    public void AttributeAndPropertyElementPathsUseTypedDrawingCalls()
    {
        using var platform = SkiaScope();
        foreach (var xaml in new[] { "<Path " + ResourceProjectFixture.Namespace + " Data='M0,0L10,10'/>",
                     "<Path " + ResourceProjectFixture.Namespace + "><Path.Data>M0,0L10,10</Path.Data></Path>" })
        {
            var fixture = new ResourceProjectFixture([("Path.axaml", xaml)]);
            var path = Assert.IsType<global::Avalonia.Controls.Shapes.Path>(fixture.Build("Path.axaml"));
            Equal(Geometry.Parse("M0,0L10,10"), path.Data!);
            Assert.DoesNotContain(".Parse(", fixture.Result.Documents.Single().Output.Source, StringComparison.Ordinal);
        }
    }

    [AvaloniaTheory]
    [InlineData("M")]
    [InlineData("M1")]
    [InlineData("M0,0X1,2")]
    [InlineData("F2")]
    [InlineData("M0,0 A1,2 0 2 0 3,4")]
    [InlineData("M0,0 A1,2 0 0 2 3,4")]
    [InlineData("M0,0 L1e")]
    [InlineData("M+1,2")]
    [InlineData("M0,0L1e2,3")]
    public void MalformedPathsBecomeDiagnosticsInsteadOfRuntimeParserFailures(string path)
    {
        Assert.NotNull(Record.Exception(() => Geometry.Parse(path)));
        Invalid(path);
    }

    [AvaloniaTheory]
    [InlineData("M0,0z1")]
    [InlineData("z,1")]
    public void NonConsumingCloseCommandsAreRejectedWithoutHangingTheCompiler(string path) => Invalid(path);

    private static void Invalid(string path)
    {
        var fixture = new ResourceProjectFixture([("Invalid.axaml", "<Path " + ResourceProjectFixture.Namespace + " Data='" + path + "'/>")]);
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, error => error.Code == "XG3004" && error.Span.Length > 0);
    }

    private static IDisposable SkiaScope()
    {
        // Avalonia removes this test setup API from its public reference assembly.
        // Use a temporary locator scope so real Skia geometry comparisons do not
        // change the headless platform used by the rest of the test suite.
        var enter = typeof(AvaloniaLocator).GetMethod("EnterScope", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var scope = (IDisposable)enter.Invoke(null, null)!;
        try { global::Avalonia.Skia.SkiaPlatform.Initialize(); return scope; }
        catch { scope.Dispose(); throw; }
    }

    private static void Equal(Geometry expected, Geometry actual)
    {
        Assert.Equal(expected.Bounds, actual.Bounds);
        Assert.Equal(expected.ContourLength, actual.ContourLength);
        var pen = new Pen(Brushes.Black, 2);
        Assert.Equal(expected.GetRenderBounds(pen), actual.GetRenderBounds(pen));
        for (var x = -10; x <= 110; x += 5)
            for (var y = -30; y <= 60; y += 5)
            {
                Assert.Equal(expected.FillContains(new Point(x, y)), actual.FillContains(new Point(x, y)));
                Assert.Equal(expected.StrokeContains(pen, new Point(x, y)), actual.StrokeContains(pen, new Point(x, y)));
            }
    }

    private static void Figures(PathFigures expected, PathFigures actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index]; var right = actual[index];
            Assert.Equal(left.StartPoint, right.StartPoint); Assert.Equal(left.IsClosed, right.IsClosed); Assert.Equal(left.IsFilled, right.IsFilled);
            Assert.Equal(left.Segments!.Count, right.Segments!.Count);
            for (var segment = 0; segment < left.Segments.Count; segment++)
            {
                var a = left.Segments[segment]; var b = right.Segments[segment];
                Assert.Equal(a.GetType(), b.GetType()); Assert.Equal(a.IsStroked, b.IsStroked);
                foreach (var property in a.GetType().GetProperties().Where(property => property.DeclaringType == a.GetType() && property.CanRead))
                    Assert.Equal(property.GetValue(a), property.GetValue(b));
            }
        }
    }
}
