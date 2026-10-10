using System.Security;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class StructuredLiteralTests
{
    internal const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";
    internal const string Path = "/source/Structured.axaml";

    [AvaloniaTheory]
    [InlineData("Thickness", "1")]
    [InlineData("Thickness", "1,2")]
    [InlineData("Thickness", "1 2 3 4")]
    [InlineData("Thickness", "1 invalid")]
    [InlineData("Thickness", "1 2 invalid")]
    [InlineData("Thickness", "NaN Infinity -Infinity -0")]
    [InlineData("CornerRadius", "1")]
    [InlineData("CornerRadius", "1,2")]
    [InlineData("CornerRadius", "1 2 3 4")]
    [InlineData("CornerRadius", "1 invalid")]
    [InlineData("CornerRadius", "1 2 invalid")]
    [InlineData("Point", "1,2")]
    [InlineData("Point", "  -1.5e2 , +3.5e-2  ")]
    [InlineData("Point", "-0,-Infinity")]
    [InlineData("Point", "1\u00a02")]
    [InlineData("Vector", "1,2")]
    [InlineData("Vector", "NaN,Infinity")]
    [InlineData("Size", "-1,2")]
    [InlineData("Size", "NaN,-0")]
    [InlineData("Matrix", "1 2 3 4 5 6")]
    [InlineData("Matrix", "1 2 3 4 5 6 7")]
    [InlineData("Matrix", "1 2 3 4 5 6 7 8")]
    [InlineData("Matrix", "1 2 3 4 5 6 7 8 9")]
    [InlineData("Matrix", "1 2 3 4 5 6 invalid")]
    [InlineData("Matrix", "1 2 3 4 5 6 7 invalid")]
    [InlineData("Matrix", "1 2 3 4 5 6 7 8 invalid")]
    [InlineData("RelativePoint", "1,2")]
    [InlineData("RelativePoint", "25%,75%")]
    [InlineData("RelativePoint", "-25%%,150%%")]
    [InlineData("RelativePoint", "NaN%,Infinity%")]
    [InlineData("Rect", "1,2,3,4")]
    [InlineData("Rect", "-1 -2 -3 -4")]
    [InlineData("Rect", "NaN Infinity -Infinity -0")]
    [InlineData("PixelRect", "1,2,3,4")]
    [InlineData("PixelRect", "-2147483648 2147483647 -1 +2")]
    public void StructuredValuesPreserveThePinnedComponents(string property, string literal)
    {
        var xaml = Root(property, literal);
        var expected = Read(Baseline(xaml), property);
        var actual = Read(AvaloniaCompilation.Build(xaml, true, Path), property);
        Equivalent(expected, actual);
    }

    [AvaloniaTheory]
    [InlineData("Thickness", "1,2,3")]
    [InlineData("Thickness", "1,2,3,4,5")]
    [InlineData("Thickness", "1 invalid 3")]
    [InlineData("CornerRadius", "1,2,3")]
    [InlineData("CornerRadius", "1,2,3,invalid")]
    [InlineData("Point", "1")]
    [InlineData("Point", "1,2,3")]
    [InlineData("Point", "1,,2")]
    [InlineData("Point", ",1,2")]
    [InlineData("Point", "1,2,")]
    [InlineData("Vector", "1;2")]
    [InlineData("Size", "1,no")]
    [InlineData("Matrix", "1 2 3 4 5")]
    [InlineData("Matrix", "1 2 3 4 5 6 7 8 9 10")]
    [InlineData("Matrix", "1 2 3 4 5 6 invalid 8")]
    [InlineData("RelativePoint", "25%,1")]
    [InlineData("RelativePoint", "1,25%")]
    [InlineData("RelativePoint", "1,2,3")]
    [InlineData("Rect", "1,2,3")]
    [InlineData("Rect", "1,2,3,4,")]
    [InlineData("Rect", "1,2,3,4,5")]
    [InlineData("PixelRect", "1.5,2,3,4")]
    [InlineData("PixelRect", "2147483648,0,1,2")]
    [InlineData("PixelRect", "1,,2,3,4")]
    public void InvalidStructuredValuesFailDuringBinding(string property, string literal)
    {
        var xaml = Root(property, literal);
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml, true, Path).Error);
        var fixture = new ResourceProjectFixture(new[] { ("Structured.axaml", xaml) });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3004");
    }

    [AvaloniaTheory]
    [InlineData("Trimming", "None")]
    [InlineData("Trimming", "characterellipsis")]
    [InlineData("Trimming", "LeadingCharacterEllipsis")]
    [InlineData("Trimming", "pathsegmentellipsis")]
    [InlineData("Decorations", "Underline")]
    [InlineData("Decorations", "sTrIkEtHrOuGh")]
    [InlineData("Decorations", "Baseline")]
    [InlineData("Transparency", "transparent")]
    [InlineData("Transparency", "AcRyLiCbLuR")]
    [InlineData("Theme", "Dark")]
    [InlineData("Theme", " Light ")]
    [InlineData("Theme", "Default")]
    public void StaticLiteralsResolveThePinnedValue(string property, string literal)
    {
        var xaml = Root(property, literal);
        var expected = Read(Baseline(xaml), property);
        var actual = Read(AvaloniaCompilation.Build(xaml, true, Path), property);
        Assert.Equal(expected.GetType(), actual.GetType());
        if (expected.GetType().IsValueType) Assert.Equal(expected, actual);
        else Assert.Same(expected, actual);
        Assert.Null(SourceInfo.GetXamlSourceInfo(actual));
    }

    [AvaloniaTheory]
    [InlineData("Transparency", " Transparent ")]
    [InlineData("Transparency", "Unknown")]
    public void UnsupportedStaticNamesFailBinding(string property, string literal)
    {
        var xaml = Root(property, literal);
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml, true, Path).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Structured.axaml", xaml) }).Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("Theme", "dark")]
    [InlineData("Theme", "Unknown")]
    [InlineData("Trimming", " None ")]
    [InlineData("Decorations", "Unknown")]
    public void UnrecognizedStaticValuesRetainRuntimeConversionFailures(string property, string literal)
    {
        var xaml = Root(property, literal);
        var expected = AvaloniaUpstreamCompilation.Compile(xaml, true, Path).Error;
        Assert.NotNull(expected);
        var actual = Assert.Throws<System.Reflection.TargetInvocationException>(() => AvaloniaCompilation.Build(xaml, true, Path));
        Assert.Equal(expected.GetBaseException().GetType(), actual.GetBaseException().GetType());
        Assert.Equal(expected.GetBaseException().Message, actual.GetBaseException().Message);
    }

    [AvaloniaTheory]
    [InlineData("Thickness", "1 2 3 4")]
    [InlineData("Matrix", "1 2 3 4 5 6 7 8 9")]
    [InlineData("RelativePoint", "25%,75%")]
    [InlineData("TextTrimming", "leadingcharacterellipsis")]
    [InlineData("TextDecorationCollection", "Underline")]
    [InlineData("WindowTransparencyLevel", "transparent")]
    [InlineData("ThemeVariant", "Dark")]
    public void ResourceLocationsFollowStructuredAndStaticValues(string type, string text)
    {
        var xaml = "<ResourceDictionary " + Ns + ">\n  <" + type + " x:Key='item'>" + Escape(text) + "</" + type + ">\n</ResourceDictionary>";
        var expected = Assert.IsType<ResourceDictionary>(Baseline(xaml));
        var actual = Assert.IsType<ResourceDictionary>(AvaloniaCompilation.Build(xaml, true, Path));
        Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, "item"), SourceInfo.GetXamlSourceInfo(actual, "item"));
        Assert.Equal(expected["item"]!.GetType(), actual["item"]!.GetType());
        Assert.Equal(SourceInfo.GetXamlSourceInfo(expected["item"]!), SourceInfo.GetXamlSourceInfo(actual["item"]!));
    }

    [AvaloniaFact]
    public void StringObjectMatrixConversionRetainsRuntimePerspective()
    {
        var xaml = "<ResourceDictionary " + Ns + "><Matrix x:Key='item'><x:String>1 2 3 4 5 6 7 8 9</x:String></Matrix></ResourceDictionary>";
        var expected = Assert.IsType<Matrix>(Assert.IsType<ResourceDictionary>(Baseline(xaml))["item"]);
        var actual = Assert.IsType<Matrix>(Assert.IsType<ResourceDictionary>(AvaloniaCompilation.Build(xaml, true, Path))["item"]);
        Equivalent(expected, actual);
        Assert.Equal(7d, actual.M13);
        Assert.Equal(8d, actual.M23);
        Assert.Equal(9d, actual.M33);
    }

    internal static object Baseline(string xaml, bool metadata = true)
    {
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, metadata, Path);
        Assert.Null(baseline.Error);
        return baseline.Root!;
    }
    internal static string Escape(string text) => SecurityElement.Escape(text)!;
    private static string Root(string property, string literal) => "<t:StructuredLiteralProbe " + Ns + "\n  " + property + "='" + Escape(literal) + "'/>";
    private static object Read(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value)!;
    private static void Equivalent(object expected, object actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        if (expected is RelativePoint relative)
        {
            var other = Assert.IsType<RelativePoint>(actual);
            Assert.Equal(relative.Unit, other.Unit); Equivalent(relative.Point, other.Point); return;
        }
        foreach (var property in expected.GetType().GetProperties().Where(property => property.PropertyType == typeof(double)))
            Assert.Equal(BitConverter.DoubleToInt64Bits((double)property.GetValue(expected)!), BitConverter.DoubleToInt64Bits((double)property.GetValue(actual)!));
        foreach (var property in expected.GetType().GetProperties().Where(property => property.PropertyType == typeof(int)))
            Assert.Equal(property.GetValue(expected), property.GetValue(actual));
    }
}

public sealed class StructuredLiteralProbe
{
    public Thickness Thickness { get; set; }
    public CornerRadius CornerRadius { get; set; }
    public Point Point { get; set; }
    public Vector Vector { get; set; }
    public Size Size { get; set; }
    public Matrix Matrix { get; set; }
    public RelativePoint RelativePoint { get; set; }
    public Rect Rect { get; set; }
    public PixelRect PixelRect { get; set; }
    public TextTrimming? Trimming { get; set; }
    public TextDecorationCollection? Decorations { get; set; }
    public WindowTransparencyLevel Transparency { get; set; }
    public ThemeVariant? Theme { get; set; }
}
