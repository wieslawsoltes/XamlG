using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Security;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class LiteralConstructionTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests' xmlns:s='clr-namespace:System;assembly=System.Private.CoreLib' xmlns:u='clr-namespace:System;assembly=System.Private.Uri'";
    private const string Path = "/source/Literals.axaml";

    [AvaloniaTheory]
    [InlineData("Brush", "Red", true)]
    [InlineData("Brush", "#123456", true)]
    [InlineData("Cursor", "Hand", true)]
    [InlineData("Cursor", "hand", false)]
    [InlineData("Cursor", " Hand ", false)]
    [InlineData("Cursor", "9", true)]
    [InlineData("Cursor", "4294967296", true)]
    [InlineData("Uri", "/assets/item", true)]
    [InlineData("Uri", " //server/item ", true)]
    [InlineData("Uri", " ../item ", true)]
    [InlineData("Uri", "https://example.test/a%20b", true)]
    [InlineData("Row", "2*", true)]
    [InlineData("Row", "auto", true)]
    [InlineData("Row", "1,024", true)]
    [InlineData("Column", "Auto", true)]
    [InlineData("Column", " 2.5*", true)]
    [InlineData("Column", "2e2", true)]
    [InlineData("Rows", "Auto,2*", false)]
    [InlineData("Font", "Arial", false)]
    public void ReferenceLiteralAttributesPreserveValuesAndConstructionMetadata(string property, string literal, bool located)
    {
        var xaml = Root("\n  " + property + "='" + Escape(literal) + "'");
        var expected = Read(Baseline(xaml), property);
        Assert.NotNull(expected);
        Assert.Equal(located ? new SourceInfo(2, 3, Path) : null, SourceInfo.GetXamlSourceInfo(expected));
        var actual = Read(AvaloniaCompilation.Build(xaml, true, Path), property);
        Equivalent(expected, actual);
        Assert.Equal(located ? new SourceInfo(2, 3, Path) : null, SourceInfo.GetXamlSourceInfo(actual));
    }

    [AvaloniaTheory]
    [InlineData("Red")]
    [InlineData("pAlEvIoLeTrEd")]
    [InlineData("Transparent")]
    [InlineData("#123")]
    [InlineData("#4123")]
    [InlineData("#00112233")]
    [InlineData("#aAbBcC")]
    [InlineData("# 12345")]
    [InlineData("rgb(10,20,30)")]
    [InlineData("RGBA(10,20,30,0.5)")]
    [InlineData("rgba(10,20,30)")]
    [InlineData("rgb(10,20,30,0.5)")]
    [InlineData("rgb(10%,20%,30%)")]
    [InlineData("rgba(0%,100%,50%,25%)")]
    [InlineData("rgb(10%ignored,20,30)")]
    [InlineData("rgb(10.0,20,30) ")]
    [InlineData("hsl(120,100%,50%)")]
    [InlineData("hsla(240,1,0.5,0.25)")]
    [InlineData("hsv(30,100%,100%)")]
    [InlineData("hsva(30,1,1,50%)")]
    public void BrushAndColorLiteralsUseThePinnedColorGrammar(string literal)
    {
        var xaml = Root("Brush='" + Escape(literal) + "' Color='" + Escape(literal) + "'");
        var expected = Assert.IsType<LiteralConstructionProbe>(Baseline(xaml));
        var actual = Assert.IsType<LiteralConstructionProbe>(AvaloniaCompilation.Build(xaml, true, Path));
        Equivalent(expected.Brush!, actual.Brush!);
        Assert.Equal(expected.Color, actual.Color);
    }

    [AvaloniaFact]
    public void KnownAndComputedColorsAreCompiledToNumericValues()
    {
        var literals = typeof(Colors).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(Color)).Select(property => property.Name.ToLowerInvariant())
            .Concat(new[]
            {
                "#123", "#4123", "#00112233", "#aAbBcC", "# 12345",
                "rgba(10,20,30,0.5)", "rgb(10%,20%,30%)", "rgba(0%,100%,50%,25%)",
                "rgb(10%ignored,20,30)", "rgb(10.0,20,30) ", "hsl(120,100%,50%)",
                "hsla(240,1,0.5,0.25)", "hsv(30,100%,100%)", "hsva(30,1,1,50%)",
                "hsl(360,150%,-10%)", "hsva(-60,-1,2,2)", "rgba(50%,50%,50%,50%)"
            }).ToArray();
        var entries = literals.Select((literal, index) => "<Color x:Key='" + index + "'>" + Escape(literal) + "</Color>");
        var fixture = new ResourceProjectFixture(new[] { ("Colors.axaml", ResourceProjectFixture.Dictionary(string.Join("", entries))) });
        var root = Assert.IsType<ResourceDictionary>(fixture.Build("Colors.axaml"));
        for (var index = 0; index < literals.Length; index++)
            Assert.Equal(Color.Parse(literals[index]), Assert.IsType<Color>(root[index.ToString(CultureInfo.InvariantCulture)]));
        Assert.DoesNotContain("global::Avalonia.Media.Color.Parse(", fixture.Result.Documents.Single().Output.Source, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData("HslColor", "hsl(120,100%,50%)")]
    [InlineData("HslColor", "hsla(240,1,0.5,0.25)")]
    [InlineData("HslColor", "hsl(360,150%,-10%)")]
    [InlineData("HsvColor", "hsv(30,100%,100%)")]
    [InlineData("HsvColor", "hsva(30,1,1,50%)")]
    [InlineData("HsvColor", "hsva(-60,-1,2,2)")]
    public void ColorModelsAreCompiledWithoutRuntimeTextParsing(string type, string literal)
    {
        var fixture = new ResourceProjectFixture(new[] { ("Model.axaml", ResourceProjectFixture.Dictionary(
            "<" + type + " x:Key='value'>" + Escape(literal) + "</" + type + ">")) });
        var root = Assert.IsType<ResourceDictionary>(fixture.Build("Model.axaml"));
        if (type == "HslColor") Assert.Equal(HslColor.Parse(literal), Assert.IsType<HslColor>(root["value"]));
        else Assert.Equal(HsvColor.Parse(literal), Assert.IsType<HsvColor>(root["value"]));
        Assert.DoesNotContain("global::Avalonia.Media." + type + ".Parse(", fixture.Result.Documents.Single().Output.Source, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrushLiteralsCreateIndependentInstances(bool metadata)
    {
        var xaml = "<StackPanel " + Ns + ">\n  <Border Background='Red'/>\n  <Border Background='Red'/>\n</StackPanel>";
        foreach (var value in Both(xaml, metadata))
        {
            var root = Assert.IsType<StackPanel>(value);
            var first = Assert.IsType<ImmutableSolidColorBrush>(Assert.IsType<Border>(root.Children[0]).Background);
            var second = Assert.IsType<ImmutableSolidColorBrush>(Assert.IsType<Border>(root.Children[1]).Background);
            Assert.NotSame(first, second);
            Assert.NotSame(Brushes.Red, first);
            Assert.Equal(metadata ? new SourceInfo(2, 11, Path) : null, SourceInfo.GetXamlSourceInfo(first));
            Assert.Equal(metadata ? new SourceInfo(3, 11, Path) : null, SourceInfo.GetXamlSourceInfo(second));
        }
    }

    [AvaloniaTheory]
    [InlineData("<SolidColorBrush x:Key='item'>Red</SolidColorBrush>")]
    [InlineData("<SolidColorBrush x:Key='item'><x:String>Red</x:String></SolidColorBrush>")]
    [InlineData("<SolidColorBrush x:Key='item'><![CDATA[#123456]]></SolidColorBrush>")]
    [InlineData("<SolidColorBrush x:Key='item'>&#x52;ed</SolidColorBrush>")]
    [InlineData("<FontFamily x:Key='item'>Arial</FontFamily>")]
    [InlineData("<RowDefinition x:Key='item'>2*</RowDefinition>")]
    [InlineData("<ColumnDefinition x:Key='item'>Auto</ColumnDefinition>")]
    [InlineData("<RowDefinitions x:Key='item'>Auto,2*</RowDefinitions>")]
    [InlineData("<Cursor x:Key='item'>Hand</Cursor>")]
    [InlineData("<Cursor x:Key='item'>hand</Cursor>")]
    [InlineData("<u:Uri x:Key='item'>/assets/item</u:Uri>")]
    [InlineData("<s:TimeSpan x:Key='item'>0.25</s:TimeSpan>")]
    [InlineData("<GridLength x:Key='item'>2*</GridLength>")]
    public void ConvertedResourcesKeepKeyAndValueLocations(string entry)
    {
        var xaml = "<ResourceDictionary " + Ns + ">\n  " + entry + "\n</ResourceDictionary>";
        var expected = Assert.IsType<ResourceDictionary>(Baseline(xaml));
        var actual = Assert.IsType<ResourceDictionary>(AvaloniaCompilation.Build(xaml, true, Path));
        var keyInfo = SourceInfo.GetXamlSourceInfo(expected, "item");
        Assert.NotNull(keyInfo);
        Assert.Equal(keyInfo, SourceInfo.GetXamlSourceInfo(actual, "item"));
        Equivalent(expected["item"]!, actual["item"]!);
    }

    [AvaloniaFact]
    public void ExplicitStringObjectsDoNotUseRowDefinitionIntrinsics()
    {
        var xaml = "<ResourceDictionary " + Ns + "><RowDefinition x:Key='item'><x:String>2*</x:String></RowDefinition></ResourceDictionary>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml, true, Path).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Literals.axaml", xaml) }).Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("&#x9;&#xA;&#xD;")]
    public void WhitespaceOnlyScalarAttributesDoNotRunSettersOrConverters(string whitespace)
    {
        var xaml = Root("Uri='" + whitespace + "' Converted='" + whitespace + "' Brush='" + whitespace +
            "' Length='" + whitespace + "' Text='" + whitespace + "' Object='" + whitespace + "'");
        LiteralConstructionProbe? expected = null;
        foreach (var value in Both(xaml, true))
        {
            var root = Assert.IsType<LiteralConstructionProbe>(value);
            Assert.Equal("/initial", root.Uri!.OriginalString);
            Assert.Equal(0, root.UriAssignments);
            Assert.Null(root.Converted);
            Assert.Null(root.Brush);
            Assert.Empty(LiteralConverter.Events);
            Assert.NotNull(root.Text);
            Assert.Equal(root.Text, root.Object);
            if (expected != null) Assert.Equal(expected.Text, root.Text);
            expected = root;
        }
    }

    [AvaloniaTheory]
    [InlineData("Brush", "Red")]
    [InlineData("Row", "2*")]
    [InlineData("Uri", "/assets/item")]
    [InlineData("Cursor", "Hand")]
    public void PropertyElementTextRetainsItsTextLocation(string property, string literal)
    {
        var xaml = "<t:LiteralConstructionProbe " + Ns + ">\n  <t:LiteralConstructionProbe." + property + ">" + literal + "</t:LiteralConstructionProbe." + property + ">\n</t:LiteralConstructionProbe>";
        Equivalent(Read(Baseline(xaml), property), Read(AvaloniaCompilation.Build(xaml, true, Path), property));
    }

    [AvaloniaTheory]
    [InlineData("0.25")]
    [InlineData("-0.25")]
    [InlineData("1")]
    [InlineData("1e-7")]
    [InlineData("1,000.25")]
    [InlineData("00:00:00.1234567")]
    [InlineData(" 2.03:04:05 ")]
    public void TimeSpansPreserveTicksAndShorthandSeconds(string literal)
    {
        var xaml = Root("Duration='" + literal + "'");
        Assert.Equal(Assert.IsType<LiteralConstructionProbe>(Baseline(xaml)).Duration,
            Assert.IsType<LiteralConstructionProbe>(AvaloniaCompilation.Build(xaml, true, Path)).Duration);
    }

    [AvaloniaTheory]
    [InlineData("Row", "-1")]
    [InlineData("Row", "NaN")]
    [InlineData("Column", "Infinity")]
    [InlineData("Length", " Auto ")]
    [InlineData("Length", "2* ")]
    [InlineData("Length", "invalid")]
    [InlineData("Color", "not-a-color")]
    [InlineData("Color", "rEbEcCaPuRpLe")]
    [InlineData("Color", "rgb(256,0,0)")]
    [InlineData("Color", "hsl(120%,1,1)")]
    [InlineData("Color", "#12")]
    [InlineData("Uri", "http://")]
    [InlineData("Duration", "NaN")]
    [InlineData("Duration", "Infinity")]
    [InlineData("Duration", "not-a-duration")]
    public void InvalidIntrinsicsProduceBindingDiagnostics(string property, string literal)
    {
        var xaml = Root(property + "='" + Escape(literal) + "'");
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml, true, Path).Error);
        var fixture = new ResourceProjectFixture(new[] { ("Literals.axaml", xaml) });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3004");
    }

    [AvaloniaTheory]
    [InlineData("ConcreteBrush='Red'")]
    [InlineData("ConcreteBrush='#123456'")]
    [InlineData("ConcreteBrush='rgb(10,20,30)'")]
    public void ImmutableBrushLiteralsCannotBeAssignedToConcreteMutableProperties(string attribute)
    {
        var xaml = Root(attribute);
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml, true, Path).Error);
        var fixture = new ResourceProjectFixture(new[] { ("Literals.axaml", xaml) });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG1023");
    }

    [AvaloniaTheory]
    [InlineData("Cursor", "unknown-cursor")]
    [InlineData("Brush", " Red ")]
    [InlineData("Brush", "rgb(256,0,0)")]
    [InlineData("ConcreteBrush", "not-a-color")]
    public void NonIntrinsicInputsRetainTheirRuntimeParserFailure(string property, string literal)
    {
        var xaml = Root(property + "='" + Escape(literal) + "'");
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, true, Path);
        Assert.NotNull(baseline.Error);
        var actual = Assert.Throws<TargetInvocationException>(() => AvaloniaCompilation.Build(xaml, true, Path));
        Assert.Equal(baseline.Error.GetBaseException().GetType(), actual.GetBaseException().GetType());
        Assert.Equal(baseline.Error.GetBaseException().Message, actual.GetBaseException().Message);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConvertersReceiveMetadataBeforeEvaluatingTheirInput(bool metadata, bool provided)
    {
        var xaml = Root("\n  Converted='" + (provided ? "{t:LiteralText}" : "literal") + "'");
        foreach (var value in Both(xaml, metadata))
        {
            var converted = Assert.IsType<LiteralConverted>(Assert.IsType<LiteralConstructionProbe>(value).Converted);
            Assert.Equal("literal", converted.Text);
            Assert.Null(converted.ConstructorSource);
            Assert.Equal(metadata ? new SourceInfo(2, 3, Path) : null, converted.ConverterSource);
            Assert.Null(SourceInfo.GetXamlSourceInfo(converted));
            Assert.Equal(provided ? new[] { "converter", "provider", "provide", "convert" } : new[] { "converter", "convert" }, LiteralConverter.Events);
        }
    }

    private static string Root(string attributes) => "<t:LiteralConstructionProbe " + Ns + " " + attributes + "/>";
    private static string Escape(string text) => SecurityElement.Escape(text)!;
    private static object Read(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value)!;
    private static object Baseline(string xaml, bool metadata = true)
    {
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, metadata, Path);
        Assert.Null(baseline.Error);
        return baseline.Root!;
    }
    private static IEnumerable<object> Both(string xaml, bool metadata)
    {
        LiteralConverter.Events.Clear();
        yield return Baseline(xaml, metadata);
        LiteralConverter.Events.Clear();
        yield return AvaloniaCompilation.Build(xaml, metadata, Path);
    }
    private static void Equivalent(object expected, object actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(SourceInfo.GetXamlSourceInfo(expected), SourceInfo.GetXamlSourceInfo(actual));
        switch (expected)
        {
            case ISolidColorBrush brush:
                Assert.Equal(brush.Color, Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color); break;
            case RowDefinition row:
                Assert.Equal(row.Height, Assert.IsType<RowDefinition>(actual).Height); break;
            case ColumnDefinition column:
                Assert.Equal(column.Width, Assert.IsType<ColumnDefinition>(actual).Width); break;
            case RowDefinitions rows:
                var other = Assert.IsType<RowDefinitions>(actual);
                Assert.Equal(rows.Count, other.Count);
                for (var index = 0; index < rows.Count; index++) Equivalent(rows[index], other[index]);
                break;
            case Uri uri:
                var actualUri = Assert.IsType<Uri>(actual);
                Assert.Equal(uri.IsAbsoluteUri, actualUri.IsAbsoluteUri);
                Assert.Equal(uri.OriginalString, actualUri.OriginalString); break;
            case Cursor:
                Assert.Equal(expected.ToString(), actual.ToString()); break;
            default: Assert.Equal(expected, actual); break;
        }
    }
}

public sealed class LiteralConstructionProbe
{
    public IBrush? Brush { get; set; }
    public Color Color { get; set; }
    public SolidColorBrush? ConcreteBrush { get; set; }
    public Cursor? Cursor { get; set; }
    private Uri? _uri = new("/initial", UriKind.Relative);
    public Uri? Uri { get => _uri; set { UriAssignments++; _uri = value; } }
    public int UriAssignments { get; private set; }
    public string? Text { get; set; }
    public object? Object { get; set; }
    public RowDefinition? Row { get; set; }
    public ColumnDefinition? Column { get; set; }
    public RowDefinitions? Rows { get; set; }
    public GridLength Length { get; set; }
    public FontFamily? Font { get; set; }
    public TimeSpan Duration { get; set; }
    [TypeConverter(typeof(LiteralConverter))] public LiteralConverted? Converted { get; set; }
}

public sealed record LiteralConverted(string Text, SourceInfo? ConstructorSource, SourceInfo? ConverterSource);

public sealed class LiteralConverter : TypeConverter
{
    public static List<string> Events { get; } = new();
    private readonly SourceInfo? _constructorSource;
    public LiteralConverter() { Events.Add("converter"); _constructorSource = SourceInfo.GetXamlSourceInfo(this); }
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        Events.Add("convert");
        return new LiteralConverted((string)value, _constructorSource, SourceInfo.GetXamlSourceInfo(this));
    }
}

public sealed class LiteralTextExtension
{
    public LiteralTextExtension() => LiteralConverter.Events.Add("provider");
    public string ProvideValue() { LiteralConverter.Events.Add("provide"); return "literal"; }
}
