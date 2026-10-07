using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class StyleWarningTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("<Style Selector='Button'><Setter Property='Width' Value='1'/><Setter Property='Width' Value='2'/></Style>", 1)]
    [InlineData("<ControlTheme TargetType='Button'><Setter Property='Width' Value='1'/><Setter Property='Width' Value='2'/></ControlTheme>", 1)]
    [InlineData("<t:DerivedStyle Selector='Button'><Setter Property='Width' Value='1'/><Setter Setter.Property='Width' Value='2'/></t:DerivedStyle>", 1)]
    [InlineData("<Style Selector='Button'><Setter Property='Width' Value='1'/><Setter Property='Width' Value='2'/><Setter Property='Width' Value='3'/></Style>", 2)]
    [InlineData("<Style Selector='Button'><Setter Property='Width' Value='1'/><Setter Property='Control.Width' Value='2'/></Style>", 0)]
    [InlineData("<Style Selector='Button'><Style.Setters><Setter Property='Width' Value='1'/><Setter Property='Width' Value='2'/></Style.Setters></Style>", 0)]
    [InlineData("<Style Selector='Button'><Setter Property='Width' Value='1'/><Style Selector='^:pointerover'><Setter Property='Width' Value='2'/></Style></Style>", 0)]
    public void DuplicateSetterWarningsMatchTheWrittenPropertiesAndScope(string content, int count) =>
        AssertWarnings(content, "AVLN2203", "XG3107", count);

    [AvaloniaFact]
    public void DuplicateSettersCompileAndRetainTheFrameworkRuntimeError()
    {
        var xaml = "<Button " + Ns + "><Button.Styles><Style Selector='Button'><Setter Property='Width' Value='1'/><Setter Property='Width' Value='2'/></Style></Button.Styles></Button>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        var project = new ResourceProjectFixture(new[] { ("Style.axaml", xaml) });
        var messages = new List<string>();
        foreach (var control in new[] { Assert.IsType<Button>(baseline.Root), Assert.IsType<Button>(project.Build("Style.axaml")) })
        {
            var window = new Window();
            try { messages.Add(Assert.Throws<InvalidOperationException>(() => window.Content = control).Message); }
            finally { window.Close(); }
        }
        Assert.Contains("Duplicate setter", messages[0]);
        Assert.Equal(messages[0], messages[1]);
    }

    [AvaloniaTheory]
    [InlineData("ListBox", "ListBoxItem", 1)]
    [InlineData("ComboBox", "ComboBoxItem", 1)]
    [InlineData("Menu", "MenuItem", 0)]
    [InlineData("MenuItem", "MenuItem", 0)]
    [InlineData("TabStrip", "TabStripItem", 1)]
    [InlineData("TabControl", "TabItem", 1)]
    [InlineData("TreeView", "TreeViewItem", 0)]
    public void KnownItemContainerWarningsMatchTheUpstreamContentControlRestriction(string control, string container, int count) =>
        AssertWarnings("<" + control + "><" + control + ".ItemTemplate><DataTemplate><" + container + "/></DataTemplate></" + control + ".ItemTemplate></" + control + ">", "AVLN2208", "XG3117", count);

    [AvaloniaTheory]
    [InlineData("<ListBox><ListBox.DataTemplates><DataTemplate DataType='x:Object'><ListBoxItem/></DataTemplate></ListBox.DataTemplates></ListBox>", 1)]
    [InlineData("<ListBox><ListBox.ItemTemplate><DataTemplate><t:DerivedListBoxItem/></DataTemplate></ListBox.ItemTemplate></ListBox>", 1)]
    [InlineData("<t:DerivedListBox><t:DerivedListBox.ItemTemplate><DataTemplate><ListBoxItem/></DataTemplate></t:DerivedListBox.ItemTemplate></t:DerivedListBox>", 0)]
    [InlineData("<ListBox><ListBox.ItemTemplate><DataTemplate><ComboBoxItem/></DataTemplate></ListBox.ItemTemplate></ListBox>", 1)]
    [InlineData("<ListBox><ListBox.ItemTemplate><DataTemplate><TextBox/></DataTemplate></ListBox.ItemTemplate></ListBox>", 0)]
    [InlineData("<ListBox><ListBox.ItemTemplate><DataTemplate><Border><ListBoxItem/></Border></DataTemplate></ListBox.ItemTemplate></ListBox>", 0)]
    [InlineData("<ListBox><ListBox.ItemTemplate><DataTemplate><DataTemplate.Content><ListBoxItem/></DataTemplate.Content></DataTemplate></ListBox.ItemTemplate></ListBox>", 0)]
    [InlineData("<ListBox><ListBox.Resources><DataTemplate x:Key='Item'><ListBoxItem/></DataTemplate></ListBox.Resources></ListBox>", 0)]
    public void ItemContainerWarningsRespectTheOwningPropertyAndTemplateRoot(string content, int count) =>
        AssertWarnings(content, "AVLN2208", "XG3117", count);

    [AvaloniaTheory]
    [InlineData("<Styles><Style Selector='Button'/></Styles>", 1)]
    [InlineData("<Style/>", 1)]
    [InlineData("<Style Selector=''/>", 1)]
    [InlineData("<Style Selector='Button'/>", 0)]
    [InlineData("<ControlTheme TargetType='Button'/>", 0)]
    [InlineData("<Styles x:SetterTargetType='Button'/>", 0)]
    public void StylesInMergedDictionariesMatchUpstreamWarningScopes(string style, int count) =>
        AssertWarnings("<ResourceDictionary><ResourceDictionary.MergedDictionaries>" + style + "</ResourceDictionary.MergedDictionaries></ResourceDictionary>", "AVLN2204", "XG3309", count);

    [AvaloniaFact]
    public void StylesInTheirNormalCollectionDoNotProduceResourceWarnings() =>
        AssertWarnings("<Button><Button.Styles><Styles><Style Selector='Button'/></Styles></Button.Styles></Button>", "AVLN2204", "XG3309", 0);

    private static void AssertWarnings(string content, string upstreamCode, string code, int count)
    {
        var xaml = content.Insert(content.IndexOf('>'), " " + Ns);
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        var expected = baseline.Diagnostics.Where(d => d.Id == upstreamCode).ToArray();
        Assert.Equal(count, expected.Length);
        Assert.All(expected, d => Assert.Equal(RuntimeXamlDiagnosticSeverity.Warning, d.Severity));
        var project = new ResourceProjectFixture(new[] { ("Warnings.axaml", xaml) });
        var actual = project.Result.Documents.Single().Output.Diagnostics.Where(d => d.Code == code).ToArray();
        Assert.Equal(count, actual.Length);
        Assert.All(actual, d =>
        {
            Assert.Equal(XamlSeverity.Warning, d.Severity);
            Assert.InRange(d.Span.Start, 0, xaml.Length - 1);
            Assert.InRange(d.Span.Length, 1, xaml.Length - d.Span.Start);
        });
        Assert.NotNull(project.Build("Warnings.axaml"));
    }
}

public sealed class DerivedListBox : ListBox { }
public sealed class DerivedListBoxItem : ListBoxItem { }
