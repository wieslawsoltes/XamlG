using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DeferredResourceTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceResourcesAreCreatedOnceOnDemand(bool property)
    {
        var content = "<t:DeferredResourceObject x:Key='item' Label='lazy'/>";
        var xaml = property ? "<Border " + Ns + "><Border.Resources>" + content + "</Border.Resources></Border>" : Dictionary(content);
        foreach (var root in CompileBoth(xaml))
        {
            Assert.Equal(0, DeferredResourceObject.Constructions);
            var resources = root is Border border ? border.Resources : Assert.IsType<ResourceDictionary>(root);
            var first = Assert.IsType<DeferredResourceObject>(resources["item"]);
            Assert.Equal("lazy", first.Label);
            Assert.Same(first, resources["item"]);
            Assert.Equal(1, DeferredResourceObject.Constructions);
        }
    }

    [AvaloniaTheory]
    [InlineData("True")]
    [InlineData("False")]
    [InlineData("true")]
    public void ExplicitSharingDirectivesSelectThePinnedNonSharedAdder(string shared)
    {
        foreach (var root in CompileBoth(Dictionary("<t:DeferredResourceObject x:Key='item' x:Shared='" + shared + "'/>")))
        {
            Assert.Equal(0, DeferredResourceObject.Constructions);
            var resources = Assert.IsType<ResourceDictionary>(root);
            Assert.NotSame(resources["item"], resources["item"]);
            Assert.Equal(2, DeferredResourceObject.Constructions);
        }
    }

    [AvaloniaTheory]
    [InlineData("Name='named'", "")]
    [InlineData("x:Name='named'", "")]
    [InlineData("", "<t:DeferredResourceObject.Child><Control Name='nested'/></t:DeferredResourceObject.Child>")]
    public void LiteralNamesKeepResourcesEager(string name, string body)
    {
        foreach (var root in CompileBoth(Dictionary("<t:DeferredResourceObject x:Key='item' " + name + ">" + body + "</t:DeferredResourceObject>")))
        {
            Assert.Equal(1, DeferredResourceObject.Constructions);
            Assert.IsType<DeferredResourceObject>(Assert.IsType<ResourceDictionary>(root)["item"]);
            Assert.Equal(1, DeferredResourceObject.Constructions);
        }
    }

    [AvaloniaFact]
    public void NamesInsideTemplatesDoNotForceTheOwningResourceToBeEager()
    {
        var content = "<t:DeferredResourceObject x:Key='item'><t:DeferredResourceObject.Child><DataTemplate><Control Name='nested'/></DataTemplate></t:DeferredResourceObject.Child></t:DeferredResourceObject>";
        foreach (var root in CompileBoth(Dictionary(content)))
        {
            Assert.Equal(0, DeferredResourceObject.Constructions);
            Assert.IsType<DeferredResourceObject>(Assert.IsType<ResourceDictionary>(root)["item"]);
            Assert.Equal(1, DeferredResourceObject.Constructions);
        }
    }

    [AvaloniaTheory]
    [InlineData("<x:String x:Key='item'>text</x:String>", "text")]
    [InlineData("<x:Int32 x:Key='item'>42</x:Int32>", 42)]
    [InlineData("<t:DeferredResourceTextExtension x:Key='item'/>", "provided")]
    public void StringsAndValueTypesStayEager(string content, object expected)
    {
        foreach (var root in CompileBoth(Dictionary(content)))
        {
            if (content.Contains("Extension", StringComparison.Ordinal)) Assert.Equal(1, DeferredResourceObject.Providers);
            Assert.Equal(expected, Assert.IsType<ResourceDictionary>(root)["item"]);
        }
    }

    [AvaloniaTheory]
    [InlineData("<t:DeferredResourceNullExtension x:Key='item'/>")]
    public void DeferredResourcesCanReturnNull(string content)
    {
        foreach (var root in CompileBoth(Dictionary(content)))
        {
            Assert.Equal(0, DeferredResourceObject.Providers);
            var resources = Assert.IsType<ResourceDictionary>(root);
            Assert.True(resources.TryGetValue("item", out var value));
            Assert.Null(value);
            Assert.Null(resources["item"]);
            Assert.Equal(content.Contains("Extension", StringComparison.Ordinal) ? 1 : 0, DeferredResourceObject.Providers);
        }
    }

    [AvaloniaFact]
    public void NativeKeyedNullResourcesRemainSupported()
    {
        var xaml = Dictionary("<x:Null x:Key='item'/>");
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var resources = Assert.IsType<ResourceDictionary>(new ResourceProjectFixture(new[] { ("Resources.axaml", xaml) }).Build("Resources.axaml"));
        Assert.True(resources.TryGetValue("item", out var value));
        Assert.Null(value);
        Assert.Null(resources["item"]);
    }

    [AvaloniaFact]
    public void DeferredResourcesCanFindLaterStaticResources()
    {
        var content = "<t:DeferredResourceObject x:Key='first' Child='{StaticResource second}'/><t:DeferredResourceObject x:Key='second' Label='later'/>";
        foreach (var root in CompileBoth(Dictionary(content)))
        {
            Assert.Equal(0, DeferredResourceObject.Constructions);
            var resources = Assert.IsType<ResourceDictionary>(root);
            var first = Assert.IsType<DeferredResourceObject>(resources["first"]);
            Assert.Same(resources["second"], first.Child);
            Assert.Equal(2, DeferredResourceObject.Constructions);
        }
    }

    [AvaloniaFact]
    public void DeferredResourcesDoNotDuplicateTheirCapturedParents()
    {
        foreach (var root in CompileBoth(Dictionary("<t:DeferredResourceParentsExtension x:Key='parents'/>")))
            Assert.Equal(1, Assert.IsType<ResourceDictionary>(root)["parents"]);
    }

    [AvaloniaTheory]
    [InlineData("<t:DeferredResourceObject x:Key='item' x:Shared='invalid'/>")]
    [InlineData("<t:DeferredResourceObject x:Key='item' Name='named' x:Shared='True'/>")]
    [InlineData("<x:String x:Key='item' x:Shared='False'>text</x:String>")]
    public void SharingRequiresAValidDeferredResource(string content)
    {
        var xaml = Dictionary(content);
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Resources.axaml", xaml) }).Result.Success);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompiledMergesPreserveDeferredLocalOverrides(bool notShared)
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("Imported.axaml", Dictionary("<t:DeferredResourceObject x:Key='item' Label='imported'/>")),
            ("Local.axaml", Dictionary("<ResourceDictionary.MergedDictionaries><MergeResourceInclude Source='Imported.axaml'/></ResourceDictionary.MergedDictionaries><t:DeferredResourceObject x:Key='item' Label='local'" + (notShared ? " x:Shared='False'" : "") + "/>"))
        });
        DeferredResourceObject.Constructions = 0;
        var resources = Assert.IsType<ResourceDictionary>(fixture.Build("Local.axaml"));
        Assert.Equal(0, DeferredResourceObject.Constructions);
        var first = Assert.IsType<DeferredResourceObject>(resources["item"]);
        var second = Assert.IsType<DeferredResourceObject>(resources["item"]);
        Assert.Equal("local", first.Label);
        Assert.Equal(!notShared, ReferenceEquals(first, second));
        Assert.Equal(notShared ? 2 : 1, DeferredResourceObject.Constructions);
    }

    [AvaloniaFact]
    public void NativeReferencesCanResolveNamesOutsideTheDeferredResource()
    {
        var xaml = "<StackPanel " + Ns + " x:Name='owner'><StackPanel.Resources><t:DeferredResourceObject x:Key='item' Child='{x:Reference owner}'/></StackPanel.Resources></StackPanel>";
        var root = Assert.IsType<StackPanel>(new ResourceProjectFixture(new[] { ("Resources.axaml", xaml) }).Build("Resources.axaml"));
        Assert.Same(root, Assert.IsType<DeferredResourceObject>(root.Resources["item"]).Child);
    }

    private static string Dictionary(string content) => "<ResourceDictionary " + Ns + ">" + content + "</ResourceDictionary>";

    private static IEnumerable<object> CompileBoth(string xaml)
    {
        DeferredResourceObject.Constructions = DeferredResourceObject.Providers = 0;
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return baseline.Root!;
        DeferredResourceObject.Constructions = DeferredResourceObject.Providers = 0;
        yield return new ResourceProjectFixture(new[] { ("Resources.axaml", xaml) }).Build("Resources.axaml");
    }
}

public sealed class DeferredResourceObject : INamed
{
    public DeferredResourceObject() => Constructions++;
    public static int Constructions { get; set; }
    public static int Providers { get; set; }
    public string? Name { get; set; }
    public string? Label { get; set; }
    public object? Child { get; set; }
}

public sealed class DeferredResourceTextExtension
{
    public string ProvideValue() { DeferredResourceObject.Providers++; return "provided"; }
}

public sealed class DeferredResourceNullExtension
{
    public object? ProvideValue() { DeferredResourceObject.Providers++; return null; }
}

public sealed class DeferredResourceParentsExtension
{
    public object ProvideValue(IServiceProvider services) =>
        ((IAvaloniaXamlIlParentStackProvider)services.GetService(typeof(IAvaloniaXamlIlParentStackProvider))!).Parents.OfType<ResourceDictionary>().Count();
}
