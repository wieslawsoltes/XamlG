using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.CodeAnalysis;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ResourceLinkingTests
{
    private const string Ns = ResourceProjectFixture.Namespace;
    private static string Dictionary(string body) => ResourceProjectFixture.Dictionary(body);
    private static string Include(string path, string kind = "ResourceInclude") => ResourceProjectFixture.Include(path, kind);
    [AvaloniaFact]
    public void RelativeResourceIncludesCallFactoriesAndResolveStaticResources()
    {
        var project = new ResourceProjectFixture(new[]
        {
            ("Resources/Palette.axaml", Dictionary("<SolidColorBrush x:Key='accent' Color='#336699'/>")),
            ("Views/Main.axaml", "<StackPanel " + Ns + "><StackPanel.Resources>" + Dictionary(Include("../Resources/Palette.axaml")) + "</StackPanel.Resources><Border Background='{StaticResource accent}'/></StackPanel>")
        });
        var root = Assert.IsType<StackPanel>(project.Build("Views/Main.axaml"));
        var border = Assert.IsType<Border>(Assert.Single(root.Children));
        Assert.Equal(Color.Parse("#336699"), Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
        var source = project.Result.Documents.Single(d => d.Input.LogicalPath == "Views/Main.axaml").Output.Source;
        Assert.Contains(".Build(global::XamlG.Runtime.XamlResourceServices.Enter", source);
        Assert.DoesNotContain("new global::Avalonia.Markup.Xaml.Styling.ResourceInclude", source);
        Assert.DoesNotContain("AvaloniaXamlLoader", source);
    }
    [AvaloniaFact]
    public void StylesCanBeIncludedAndAppliedWithoutTheBinaryXamlLoader()
    {
        var project = new ResourceProjectFixture(new[]
        {
            ("Styles.axaml", "<Styles " + Ns + "><Style Selector='Button'><Setter Property='Width' Value='137'/></Style></Styles>"),
            ("Main.axaml", "<StackPanel " + Ns + "><StackPanel.Styles><StyleInclude Source='Styles.axaml'/></StackPanel.Styles><Button/></StackPanel>")
        });
        var root = (StackPanel)project.Build("Main.axaml"); var window = new Window { Content = root };
        try { window.Show(); window.UpdateLayout(); Assert.Equal(137, ((Button)root.Children[0]).Width); }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void MergeIncludesPreserveOrderAndLocalOverrides()
    {
        var project = new ResourceProjectFixture(new[]
        {
            ("First.axaml", Dictionary("<x:Int32 x:Key='shared'>1</x:Int32><x:String x:Key='first'>retained</x:String>")),
            ("Second.axaml", Dictionary("<x:Int32 x:Key='shared'>2</x:Int32>")),
            ("Main.axaml", Dictionary("<ResourceDictionary.MergedDictionaries><MergeResourceInclude Source='First.axaml'/><MergeResourceInclude Source='Second.axaml'/></ResourceDictionary.MergedDictionaries><x:Int32 x:Key='shared'>3</x:Int32>"))
        });
        var root = (ResourceDictionary)project.Build("Main.axaml");
        Assert.Equal(3, root["shared"]); Assert.Equal("retained", root["first"]); Assert.Empty(root.MergedDictionaries);
    }
    [AvaloniaFact]
    public void ThemeDictionariesMergeByVariantWithoutDiscardingEarlierKeys()
    {
        string Theme(string body) => "<ResourceDictionary.ThemeDictionaries><ResourceDictionary x:Key='{x:Static ThemeVariant.Dark}'>" + body + "</ResourceDictionary></ResourceDictionary.ThemeDictionaries>";
        var project = new ResourceProjectFixture(new[]
        {
            ("Base.axaml", Dictionary(Theme("<x:Int32 x:Key='shared'>1</x:Int32><x:String x:Key='base'>kept</x:String>"))),
            ("Main.axaml", Dictionary(Include("Base.axaml", "MergeResourceInclude") + Theme("<x:Int32 x:Key='shared'>4</x:Int32>")))
        });
        var root = (ResourceDictionary)project.Build("Main.axaml"); var dark = Assert.IsType<ResourceDictionary>(root.ThemeDictionaries[ThemeVariant.Dark]);
        Assert.Equal("kept", dark["base"]); Assert.Equal(4, dark["shared"]);
    }
    [AvaloniaFact]
    public void ReferencedExportsWithTheSameLogicalPathRemainUnambiguous()
    {
        var name = "ResourceLibrary_" + Guid.NewGuid().ToString("N");
        var library = new ResourceProjectFixture(new[] { ("Theme.axaml", Dictionary("<x:String x:Key='shared'>library</x:String>")) }, name);
        var image = library.Emit(); ResourceProjectFixture.Load(image);
        var app = new ResourceProjectFixture(new[] { ("Theme.axaml", Dictionary(Include("avares://" + name + "/Theme.axaml"))) }, references: new[] { MetadataReference.CreateFromImage(image) });
        var root = (ResourceDictionary)app.Build("Theme.axaml");
        Assert.True(root.TryGetResource("shared", null, out var value)); Assert.Equal("library", value);
        Assert.NotEqual(library.Result.Documents[0].Output.FactoryTypeName, app.Result.Documents[0].Output.FactoryTypeName);
        Assert.Contains(app.Result.Resources.Resources, r => r.ExternalFactory != null);
    }
    [AvaloniaFact]
    public void IncludeInstancesAreFreshAndOwnedByTheContainingSession()
    {
        var project = new ResourceProjectFixture(new[]
        {
            ("Resources.axaml", Dictionary("<x:String x:Key='key'>value</x:String>")),
            ("Main.axaml", Dictionary("<ResourceDictionary.MergedDictionaries><ResourceInclude Source='Resources.axaml'/><ResourceInclude Source='Resources.axaml'/></ResourceDictionary.MergedDictionaries>"))
        });
        var root = (ResourceDictionary)project.Build("Main.axaml");
        Assert.NotSame(root.MergedDictionaries[0], root.MergedDictionaries[1]);
        Assert.True(XamlRuntimeSession.TryGet(root.MergedDictionaries[0], out var included));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose(); Assert.True(included!.IsDisposed);
    }
    [Fact]
    public void CyclesAndTheirDependentsFailButIndependentDocumentsStillEmit()
    {
        var project = new ResourceProjectFixture(new[]
        {
            ("A.axaml", Dictionary(Include("B.axaml"))), ("B.axaml", Dictionary(Include("A.axaml"))),
            ("Caller.axaml", Dictionary(Include("A.axaml"))), ("Unrelated.axaml", Dictionary(string.Empty))
        });
        Assert.False(project.Result.Success);
        Assert.True(project.Result.Documents.Single(d => d.Input.LogicalPath == "Unrelated.axaml").Output.Success);
        foreach (var result in project.Result.Documents.Where(d => d.Input.LogicalPath != "Unrelated.axaml"))
        { Assert.Contains(result.Output.Diagnostics, d => d.Code == "XG3304"); Assert.Empty(result.Output.Source); }
    }
    [Fact]
    public void IncludesWithACompilationFailurePropagateADiagnosticToCallers()
    {
        var project = new ResourceProjectFixture(new[]
        {
            ("Bad.axaml", Dictionary("<x:Int32 x:Key='bad'>not an integer</x:Int32>")),
            ("Main.axaml", Dictionary(Include("Bad.axaml")))
        });
        Assert.False(project.Result.Success);
        Assert.Contains(project.Result.Documents.Single(d => d.Input.LogicalPath == "Main.axaml").Output.Diagnostics, d => d.Code == "XG3305");
    }
    [Theory]
    [InlineData("<ResourceInclude/>", "XG3302")]
    [InlineData("<ResourceInclude Source='Missing.axaml'/>", "XG3301")]
    [InlineData("<ResourceInclude Source='{Binding}'/>", "XG3302")]
    [InlineData("<MergeResourceInclude Source='Base.axaml'/><ResourceInclude Source='Base.axaml'/>", "XG3307")]
    public void InvalidIncludesHaveCompilerDiagnostics(string content, string code)
    {
        var project = new ResourceProjectFixture(new[]
        {
            ("Base.axaml", Dictionary(string.Empty)),
            ("Main.axaml", Dictionary("<ResourceDictionary.MergedDictionaries>" + content + "</ResourceDictionary.MergedDictionaries>"))
        });
        Assert.Contains(project.Result.Documents.Single(d => d.Input.LogicalPath == "Main.axaml").Output.Diagnostics, d => d.Code == code);
    }
}
