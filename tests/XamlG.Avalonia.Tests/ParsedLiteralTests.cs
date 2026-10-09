using System.Globalization;
using System.Reflection;
using System.Security;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class ParsedLiteralTests
{
    public static TheoryData<Type, string[]> ValidCases => new()
    {
        { typeof(PixelPoint), ["1,2", " -1  +2 ", "-2147483648,2147483647"] },
        { typeof(PixelSize), ["1,2", " -1  +2 ", "-2147483648,2147483647"] },
        { typeof(Vector3D), ["1,2,3", "-0,-0,-0", "NaN,Infinity,-Infinity", "1e2 -.25 +4"] },
        { typeof(RelativeScalar), ["0", "-0", "  -0%  ", "25%%", " 1,234.5 ", "NaN%", "-Infinity%"] },
        { typeof(RelativeRect), ["1,2,3,4", "10%,20%,30%,40%", "-0%,-0%,-0%,-0%", "1%%,2%%,3%%,4%%", "NaN,Infinity,-Infinity,-3"] },
        { typeof(Transform), ["1,2,3,4,5,6", "1,2,3,4,5,6,7,8,9", "1,2,3,4,5,6,7", "1,2,3,4,5,6,7,8", "-0,-0,-0,-0,-0,-0,-0,-0,-0"] },
        { typeof(UnicodeRangeSegment), ["U+20", "u+3F", "U+30??", "1?2?", "0-10FFFD", "20-10", "FFFFFF"] },
        { typeof(UnicodeRange), ["U+20", "u+3F", "U+30??", "1?2?", "0-10FFFD", "20-10", "U+20-3F, 30??, FFFFFF"] },
        { typeof(OpenTypeTag), ["wght", "a", "abcde", " ab ", "éλ😀"] }
    };

    [AvaloniaTheory]
    [MemberData(nameof(ValidCases))]
    public void PublicParsersAreLoweredWithExactValuesAndSourceMetadata(Type type, string[] literals)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            foreach (var sourceInfo in new[] { false, true })
            {
                var xaml = Dictionary(type, literals);
                var fixture = new ResourceProjectFixture([("Parsed.axaml", xaml)], createSourceInfo: sourceInfo);
                var actual = Assert.IsType<ResourceDictionary>(fixture.Build("Parsed.axaml"));
                var baseline = AvaloniaUpstreamCompilation.Compile(xaml, sourceInfo, "Parsed.axaml");
                Assert.Null(baseline.Error);
                var expected = Assert.IsType<ResourceDictionary>(baseline.Root);
                var parse = type.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!;
                for (var index = 0; index < literals.Length; index++)
                {
                    var key = "v" + index;
                    Equivalent(parse.Invoke(null, [literals[index]])!, actual[key]!);
                    Equivalent(expected[key]!, actual[key]!);
                    Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, key), SourceInfo.GetXamlSourceInfo(actual, key));
                    Assert.Equal(SourceInfo.GetXamlSourceInfo(expected[key]!), SourceInfo.GetXamlSourceInfo(actual[key]!));
                }
                var output = fixture.Result.Documents.Single().Output;
                Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", output.Source);
                Assert.DoesNotContain("XamlG.Frameworks.Avalonia.Parsing", output.Source, StringComparison.Ordinal);
                if (type == typeof(Transform))
                {
                    var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == fixture.Compilation.AssemblyName);
                    var second = Assert.IsType<ResourceDictionary>(assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, [null]));
                    Assert.NotSame(actual["v0"], second["v0"]);
                    ((MatrixTransform)actual["v0"]!).Matrix = Matrix.Identity;
                    Assert.NotEqual(Matrix.Identity, ((MatrixTransform)second["v0"]!).Matrix);
                }
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    public static TheoryData<Type, string[]> InvalidCases => new()
    {
        { typeof(PixelPoint), ["1", "1,2,3", "1.5,2", "2147483648,0", "1,,2"] },
        { typeof(PixelSize), ["1", "1,2,3", "1.5,2", "2147483648,0", "1,,2"] },
        { typeof(Vector3D), ["1,2", "1,2,3,4", "1,,2,3", "no,2,3"] },
        { typeof(RelativeScalar), ["no", "%", "1% 2%"] },
        { typeof(RelativeRect), ["1,2,3", "1%,2,3,4", "1,2,3,4%", "1,2,3,4,5"] },
        { typeof(Transform), ["1,2,3,4,5", "1,2,3,4,5,6,7,8,9,10", "rotate(30deg)"] },
        { typeof(UnicodeRangeSegment), [" U+20 ", "20-3F-FF", "U+?", "G0", "3?-4F"] },
        { typeof(UnicodeRange), [" U+20 ", "20-3F-FF", "U+?", "G0", "3?-4F", "20,"] }
    };

    [AvaloniaTheory]
    [MemberData(nameof(InvalidCases))]
    public void InvalidParserInputsProduceSourceDiagnostics(Type type, string[] literals)
    {
        var parse = type.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!;
        foreach (var literal in literals)
        {
            Assert.Throws<TargetInvocationException>(() => parse.Invoke(null, [literal]));
            var fixture = new ResourceProjectFixture([("Invalid.axaml", Dictionary(type, [literal]))]);
            Assert.False(fixture.Result.Success);
            var diagnostic = Assert.Single(fixture.Result.Documents.Single().Output.Diagnostics.Where(error => error.Code == "XG3004").Distinct());
            Assert.True(diagnostic.Span.Length > 0);
        }
    }

    private static string Dictionary(Type type, string[] values) =>
        "<ResourceDictionary " + ResourceProjectFixture.Namespace + " xmlns:p='clr-namespace:" + type.Namespace +
        ";assembly=" + type.Assembly.GetName().Name + "'>" + string.Concat(values.Select((value, index) =>
            "<p:" + type.Name + " x:Key='v" + index + "'>" + SecurityElement.Escape(value) + "</p:" + type.Name + ">")) + "</ResourceDictionary>";

    private static void Equivalent(object expected, object actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        if (expected is Transform transform) { Equivalent(transform.Value, ((Transform)actual).Value); return; }
        if (expected is RelativeRect relative)
        {
            Assert.Equal(relative.Unit, ((RelativeRect)actual).Unit);
            Equivalent(relative.Rect, ((RelativeRect)actual).Rect);
            return;
        }
        if (expected is UnicodeRange range)
        {
            // Range equality contains collection identity. Compare membership,
            // including gaps, descending ranges and values beyond Unicode's limit.
            var other = (UnicodeRange)actual;
            for (var value = -1; value <= 0x110000; value++) Assert.Equal(range.IsInRange(value), other.IsInRange(value));
            foreach (var value in new[] { 0xFFFFFE, 0xFFFFFF, 0x1000000 }) Assert.Equal(range.IsInRange(value), other.IsInRange(value));
            return;
        }
        if (expected is not Rect) Assert.Equal(expected, actual); // Rect.Equals uses ==, so NaN is unequal to itself.
        foreach (var property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.PropertyType == typeof(double) && property.GetIndexParameters().Length == 0))
            Assert.Equal(BitConverter.DoubleToInt64Bits((double)property.GetValue(expected)!),
                BitConverter.DoubleToInt64Bits((double)property.GetValue(actual)!));
    }
}
