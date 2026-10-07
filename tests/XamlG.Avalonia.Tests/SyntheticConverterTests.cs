using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Security;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class SyntheticConverterTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests' xmlns:c='clr-namespace:System.Globalization;assembly=System.Private.CoreLib' xmlns:u='clr-namespace:System;assembly=System.Private.Uri' xmlns:a='clr-namespace:Avalonia.Collections;assembly=Avalonia.Base'";
    private const string Path = "/source/Converters.axaml";
    private const string BaseUri = "avares://XamlG.Avalonia.Tests/Views/Converters.axaml";

    public static IEnumerable<object[]> Values()
    {
        var values = new[]
        {
            ("Culture", "en-GB"), ("Culture", "pl-PL"), ("Uri", "/assets/item"), ("Uri", "//host/item"),
            ("Uri", "../item"), ("Uri", "https://example.test/a%20b"), ("Duration", "1"), ("Duration", "00:00:00.25"),
            ("Font", "Arial"), ("Font", "avares://Example/Fonts#Example"), ("Font", "../Fonts/#Example"), ("Points", "1,2,3,4"),
            ("Numbers", "1,2,3"), ("Words", " first , , last "), ("Alignments", "left,right"), ("OverrideCulture", "en-GB")
        };
        foreach (var (property, value) in values)
            foreach (var provided in new[] { false, true }) yield return new object[] { property, value, provided };
    }

    [AvaloniaTheory]
    [MemberData(nameof(Values))]
    public void LiteralsAndProvidedStringsKeepThePinnedConversion(string property, string text, bool provided)
    {
        var xaml = Attribute(property, text, provided);
        var expected = Read(Baseline(xaml), property);
        var actual = Read(Build(xaml), property);
        Equivalent(expected, actual);
        if (property == "Duration" && text == "1") Assert.Equal(TimeSpan.FromDays(1), actual);
        if (property == "Font" && text == "../Fonts/#Example") Assert.Equal(provided ? null : new Uri(BaseUri), Assert.IsType<FontFamily>(actual).Key!.BaseUri);
        Assert.Equal(SourceInfo.GetXamlSourceInfo(expected), SourceInfo.GetXamlSourceInfo(actual));
        Assert.Equal(provided ? 1 : 0, SyntheticTextExtension.Calls);
    }

    [AvaloniaTheory]
    [InlineData("Points", "1,2,invalid")]
    [InlineData("Points", "1,2&#x9;3,4")]
    [InlineData("Words", "a,,b,")]
    public void RuntimeListConvertersRetainTheirOwnTokenization(string property, string text)
    {
        var xaml = Attribute(property, text, true, escape: false);
        Equivalent(Read(Baseline(xaml), property), Read(Build(xaml), property));
    }

    [AvaloniaTheory]
    [InlineData("Uri", "http://[")]
    [InlineData("Culture", "bad!culture")]
    [InlineData("Duration", "0.25")]
    [InlineData("Numbers", "1,,2")]
    [InlineData("Numbers", "1,invalid")]
    [InlineData("Points", "1,2,3")]
    public void RuntimeConversionFailuresMatchThePinnedException(string property, string text)
    {
        var xaml = Attribute(property, text, true);
        var expected = AvaloniaUpstreamCompilation.Compile(xaml, true, Path, baseUri: BaseUri).Error;
        Assert.NotNull(expected);
        var fixture = new ResourceProjectFixture(new[] { ("Converters.axaml", xaml) });
        Assert.True(fixture.Result.Success, string.Join("\n", fixture.Result.Documents.Single().Output.Diagnostics));
        var actual = Record.Exception(() => Build(xaml));
        Assert.NotNull(actual);
        Assert.Equal(expected.GetBaseException().GetType(), actual.GetBaseException().GetType());
    }

    [AvaloniaTheory]
    [InlineData("Derived")]
    [InlineData("Integers")]
    [InlineData("Array")]
    [InlineData("ReadOnlyPoints")]
    public void SyntheticMappingsDoNotConvertOtherCollectionsAsAWhole(string property)
    {
        var xaml = Attribute(property, "1,2,3,4", true);
        var expected = AvaloniaUpstreamCompilation.Compile(xaml).Error;
        Assert.NotNull(expected);
        var fixture = new ResourceProjectFixture(new[] { ("Converters.axaml", xaml) });
        if (property == "ReadOnlyPoints")
        {
            Assert.StartsWith("XamlX.", expected.GetType().FullName);
            Assert.False(fixture.Result.Success);
        }
        else
        {
            Assert.IsType<FormatException>(expected.GetBaseException());
            Assert.True(fixture.Result.Success, string.Join("\n", fixture.Result.Documents.SelectMany(document => document.Output.Diagnostics)));
            var actual = Record.Exception(() => fixture.Build("Converters.axaml"));
            Assert.NotNull(actual);
            Assert.IsType<FormatException>(actual.GetBaseException());
        }
    }

    [AvaloniaTheory]
    [InlineData("Numbers", "1,invalid")]
    [InlineData("Numbers", "2147483648")]
    [InlineData("NullableNumbers", "1,invalid")]
    [InlineData("Characters", "ab,c")]
    [InlineData("Booleans", "True,invalid")]
    public void MalformedPrimitiveItemsDoNotFallThroughToTheRuntimeConverter(string property, string text)
    {
        var xaml = Attribute(property, text, false);
        var expected = AvaloniaUpstreamCompilation.Compile(xaml).Error;
        Assert.NotNull(expected);
        Assert.StartsWith("XamlX.", expected.GetType().FullName);
        var fixture = new ResourceProjectFixture(new[] { ("Converters.axaml", xaml) });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3004");
    }

    [AvaloniaTheory]
    [InlineData("Image", false, "../images/icon.png")]
    [InlineData("Image", true, "../images/icon.png")]
    [InlineData("Bitmap", false, "/images/icon.png")]
    [InlineData("Bitmap", true, "/images/icon.png")]
    [InlineData("ImageBrushSource", false, "avares://Assets/icon.png")]
    [InlineData("ImageBrushSource", true, "avares://Assets/icon.png")]
    [InlineData("Icon", false, "icons/app.ico")]
    [InlineData("Icon", true, "icons/app.ico")]
    public void ImageConvertersUseTheDocumentAssetContext(string property, bool provided, string text)
    {
        var assets = new ConverterAssetLoader();
        using var scope = assets.Install();
        var xaml = Attribute(property, text, provided);
        foreach (var compile in new Func<string, object>[] { Baseline, Build })
        {
            assets.Opened.Clear();
            var value = Read(compile(xaml), property);
            Assert.Equal(property == "Icon" ? typeof(WindowIcon) : typeof(Bitmap), value.GetType());
            var opened = Assert.Single(assets.Opened);
            Assert.Equal(text, opened.Uri.OriginalString);
            Assert.Equal(new Uri(BaseUri), opened.BaseUri);
            Assert.Null(SourceInfo.GetXamlSourceInfo(value));
            (value as IDisposable)?.Dispose();
            Assert.Equal(provided ? 1 : 0, SyntheticTextExtension.Calls);
        }
    }

    [AvaloniaTheory]
    [InlineData("Image")]
    [InlineData("Icon")]
    public void FileImageUrisBypassTheAssetLoader(string property)
    {
        var assets = new ConverterAssetLoader();
        using var scope = assets.Install();
        var xaml = Attribute(property, "file:///fixture/icon.png", false);
        foreach (var compile in new Func<string, object>[] { Baseline, Build })
        {
            var value = Read(compile(xaml), property);
            Assert.Equal(property == "Icon" ? typeof(WindowIcon) : typeof(Bitmap), value.GetType());
            Assert.Empty(assets.Opened);
            (value as IDisposable)?.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("<c:CultureInfo x:Key='item'>en-GB</c:CultureInfo>")]
    [InlineData("<c:CultureInfo x:Key='item'><t:SyntheticTextExtension Value='pl-PL'/></c:CultureInfo>")]
    [InlineData("<u:Uri x:Key='item'><x:String>/assets/item</x:String></u:Uri>")]
    [InlineData("<Bitmap x:Key='item'>images/item.png</Bitmap>")]
    [InlineData("<WindowIcon x:Key='item'>icons/app.ico</WindowIcon>")]
    [InlineData("<a:AvaloniaList x:TypeArguments='x:Int32' x:Key='item'><t:SyntheticTextExtension Value='1,2,3'/></a:AvaloniaList>")]
    public void ConvertedResourcesRetainValuesAndKeyLocations(string content)
    {
        using var scope = new ConverterAssetLoader().Install();
        var xaml = "<ResourceDictionary " + Ns + ">\n  " + content + "\n</ResourceDictionary>";
        var expected = Assert.IsType<ResourceDictionary>(Baseline(xaml));
        var actual = Assert.IsType<ResourceDictionary>(Build(xaml));
        Assert.NotNull(SourceInfo.GetXamlSourceInfo(expected, "item"));
        Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, "item"), SourceInfo.GetXamlSourceInfo(actual, "item"));
        Equivalent(expected["item"]!, actual["item"]!);
        Assert.Null(SourceInfo.GetXamlSourceInfo(actual["item"]!));
        (expected["item"] as IDisposable)?.Dispose();
        (actual["item"] as IDisposable)?.Dispose();
    }

    [AvaloniaFact]
    public void TypedStringConstructorArgumentsUseSyntheticConverters()
    {
        var xaml = "<t:SyntheticConversionProbe " + Ns + "><t:SyntheticConversionProbe.Object><t:SyntheticCultureArgument><x:Arguments><t:SyntheticTextExtension Value='en-GB'/></x:Arguments></t:SyntheticCultureArgument></t:SyntheticConversionProbe.Object></t:SyntheticConversionProbe>";
        foreach (var compile in new Func<string, object>[] { Baseline, Build })
        {
            Assert.Equal("en-GB", Assert.IsType<SyntheticCultureArgument>(Read(compile(xaml), "Object")).Value.Name);
            Assert.Equal(1, SyntheticTextExtension.Calls);
        }
    }

    private static string Attribute(string property, string text, bool provided, bool escape = true)
    {
        var value = escape ? SecurityElement.Escape(text) : text;
        if (provided) value = "{t:SyntheticText Value=&quot;" + value + "&quot;}";
        return "<t:SyntheticConversionProbe " + Ns + " " + property + "='" + value + "'/>";
    }

    private static object Baseline(string xaml)
    {
        SyntheticTextExtension.Calls = 0;
        var result = AvaloniaUpstreamCompilation.Compile(xaml, true, Path, baseUri: BaseUri);
        Assert.Null(result.Error);
        return result.Root!;
    }

    private static object Build(string xaml)
    {
        SyntheticTextExtension.Calls = 0;
        return AvaloniaCompilation.Build(xaml, true, Path, baseUri: BaseUri);
    }

    private static object Read(object root, string property) => root.GetType().GetProperty(property)!.GetValue(root)!;

    private static void Equivalent(object expected, object actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        switch (expected)
        {
            case Bitmap bitmap:
                Assert.Equal(bitmap.PixelSize, ((Bitmap)actual).PixelSize); break;
            case WindowIcon: break;
            case Uri uri:
                Assert.Equal(uri.OriginalString, ((Uri)actual).OriginalString);
                Assert.Equal(uri.IsAbsoluteUri, ((Uri)actual).IsAbsoluteUri); break;
            case IEnumerable sequence:
                Assert.Equal(sequence.Cast<object>(), ((IEnumerable)actual).Cast<object>()); break;
            default: Assert.Equal(expected, actual); break;
        }
    }
}

public sealed class SyntheticConversionProbe : Control
{
    public object? Object { get; set; }
    public CultureInfo? Culture { get; set; }
    public Uri? Uri { get; set; }
    public TimeSpan Duration { get; set; }
    public FontFamily? Font { get; set; }
    public IList<Point>? Points { get; set; }
    public AvaloniaList<int>? Numbers { get; set; }
    public AvaloniaList<string>? Words { get; set; }
    public AvaloniaList<HorizontalAlignment>? Alignments { get; set; }
    public AvaloniaList<int?>? NullableNumbers { get; set; }
    public AvaloniaList<char>? Characters { get; set; }
    public AvaloniaList<bool>? Booleans { get; set; }
    public SyntheticDerivedList? Derived { get; set; }
    public IList<int>? Integers { get; set; }
    public int[]? Array { get; set; }
    public IReadOnlyList<Point>? ReadOnlyPoints { get; set; }
    public IImage? Image { get; set; }
    public Bitmap? Bitmap { get; set; }
    public IImageBrushSource? ImageBrushSource { get; set; }
    public WindowIcon? Icon { get; set; }
    [TypeConverter(typeof(SyntheticCultureConverter))]
    public CultureInfo? OverrideCulture { get; set; }
}

public sealed class SyntheticDerivedList : AvaloniaList<int> { }

public sealed class SyntheticTextExtension
{
    public static int Calls { get; set; }
    public string Value { get; set; } = "";
    public string ProvideValue() { Calls++; return Value; }
}

public sealed class SyntheticCultureConverter : TypeConverter
{
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => CultureInfo.GetCultureInfo("de-DE");
}

public sealed class SyntheticCultureArgument
{
    public SyntheticCultureArgument(CultureInfo value) => Value = value;
    public CultureInfo Value { get; }
}

internal sealed class ConverterAssetLoader : IAssetLoader
{
    public List<(Uri Uri, Uri? BaseUri)> Opened { get; } = new();
    public IDisposable Install()
    {
        // Avalonia's reference assembly hides locator mutation. Keep this test-only
        // service substitution scoped while exercising the real public converters.
        var locator = typeof(AvaloniaLocator);
        var scope = (IDisposable)locator.GetMethod("EnterScope")!.Invoke(null, null)!;
        try
        {
            var current = locator.GetProperty("CurrentMutable")!.GetValue(null);
            locator.GetMethod("BindToSelf")!.MakeGenericMethod(typeof(IAssetLoader)).Invoke(current, new object[] { this });
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }
    public void SetDefaultAssembly(Assembly assembly) { }
    public bool Exists(Uri uri, Uri? baseUri = null) => true;
    public Stream Open(Uri uri, Uri? baseUri = null)
    {
        Opened.Add((uri, baseUri));
        // The headless rendering and windowing backends provide image/icon stubs.
        return new MemoryStream();
    }
    public (Stream stream, Assembly assembly) OpenAndGetAssembly(Uri uri, Uri? baseUri = null) => (Open(uri, baseUri), typeof(ConverterAssetLoader).Assembly);
    public Assembly? GetAssembly(Uri uri, Uri? baseUri = null) => typeof(ConverterAssetLoader).Assembly;
    public IEnumerable<Uri> GetAssets(Uri uri, Uri? baseUri) => System.Array.Empty<Uri>();
    public void InvalidateAssemblyCache(string name) { }
    public void InvalidateAssemblyCache() { }
}
