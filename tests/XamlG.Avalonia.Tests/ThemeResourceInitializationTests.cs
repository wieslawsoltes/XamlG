using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ThemeResourceInitializationTests
{
    [AvaloniaFact]
    public void OrdinaryImportsPrecedeTheFlattenedMergesThatConsumeThem()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("Palette.axaml", ResourceProjectFixture.Dictionary("<Color x:Key='accent'>Red</Color>")),
            ("Brushes.axaml", ResourceProjectFixture.Dictionary("<SolidColorBrush x:Key='brush' Color='{StaticResource accent}'/>")),
            ("Root.axaml", ResourceProjectFixture.Dictionary("<ResourceDictionary.MergedDictionaries><ResourceInclude Source='Palette.axaml'/><MergeResourceInclude Source='Brushes.axaml'/></ResourceDictionary.MergedDictionaries>"))
        });
        var root = Assert.IsType<ResourceDictionary>(fixture.Build("Root.axaml"));
        Assert.Equal(Colors.Red, Assert.IsType<SolidColorBrush>(root["brush"]).Color);
    }

    [AvaloniaFact]
    public void ThemeVariantIsInitializedBeforeItsResourceExpressions()
    {
        var root = Build("<ResourceDictionary x:Key='Light'><SolidColorBrush x:Key='brush' Color='{StaticResource accent}'/></ResourceDictionary>" +
                         "<ResourceDictionary x:Key='Dark'><SolidColorBrush x:Key='brush' Color='{StaticResource accent}'/></ResourceDictionary>");
        AssertBrush(root, ThemeVariant.Light, Colors.Red);
        AssertBrush(root, ThemeVariant.Dark, Colors.Blue);
    }

    [AvaloniaFact]
    public void MarkupExtensionThemeKeysAreEvaluatedExactlyOnce()
    {
        CountingThemeKeyExtension.Evaluations = 0;
        var root = Build("<ResourceDictionary xmlns:p='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests' x:Key='{p:CountingThemeKey}'><SolidColorBrush x:Key='brush' Color='{StaticResource accent}'/></ResourceDictionary>");
        Assert.Equal(1, CountingThemeKeyExtension.Evaluations);
        Assert.Single(root.ThemeDictionaries);
        var dictionary = root.ThemeDictionaries[ThemeVariant.Light];
        Assert.Same(ThemeVariant.Light, dictionary.Key);
        AssertBrush(root, ThemeVariant.Light, Colors.Red);
    }

    [AvaloniaFact]
    public void FlattenedMergeAlsoEvaluatesDynamicThemeKeysOnce()
    {
        CountingThemeKeyExtension.Evaluations = 0;
        var fixture = new ResourceProjectFixture(new[]
        {
            ("Base.axaml", ResourceProjectFixture.Dictionary("<x:String x:Key='earlier'>base</x:String>")),
            ("Root.axaml", ResourceProjectFixture.Dictionary("<ResourceDictionary.MergedDictionaries><MergeResourceInclude Source='Base.axaml'/></ResourceDictionary.MergedDictionaries><ResourceDictionary.ThemeDictionaries><ResourceDictionary xmlns:p='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests' x:Key='{p:CountingThemeKey}'><x:String x:Key='value'>local</x:String></ResourceDictionary></ResourceDictionary.ThemeDictionaries>"))
        });
        var root = Assert.IsType<ResourceDictionary>(fixture.Build("Root.axaml"));
        Assert.Equal(1, CountingThemeKeyExtension.Evaluations);
        Assert.Same(ThemeVariant.Light, root.ThemeDictionaries[ThemeVariant.Light].Key);
    }

    private static ResourceDictionary Build(string themes)
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("Palette.axaml", ResourceProjectFixture.Dictionary("<ResourceDictionary.ThemeDictionaries><ResourceDictionary x:Key='Light'><Color x:Key='accent'>Red</Color></ResourceDictionary><ResourceDictionary x:Key='Dark'><Color x:Key='accent'>Blue</Color></ResourceDictionary></ResourceDictionary.ThemeDictionaries>")),
            ("Root.axaml", ResourceProjectFixture.Dictionary("<ResourceDictionary.MergedDictionaries><ResourceInclude Source='Palette.axaml'/></ResourceDictionary.MergedDictionaries><ResourceDictionary.ThemeDictionaries>" + themes + "</ResourceDictionary.ThemeDictionaries>"))
        });
        return Assert.IsType<ResourceDictionary>(fixture.Build("Root.axaml"));
    }

    private static void AssertBrush(ResourceDictionary root, ThemeVariant variant, Color expected)
    {
        Assert.True(root.TryGetResource("brush", variant, out var brush));
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(brush).Color);
    }
}
