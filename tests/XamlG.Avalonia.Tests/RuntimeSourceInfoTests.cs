using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Metadata;
using Microsoft.CodeAnalysis.Diagnostics;
using XamlG.AvaloniaRuntime;
using XamlG.Frameworks.Avalonia;
using XamlRuntimeSession = XamlG.Runtime.XamlRuntimeSession;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class RuntimeSourceInfoTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";
    private const string Path = "/source/Main.axaml";

    [AvaloniaTheory]
    [InlineData(false, Path)]
    [InlineData(true, Path)]
    [InlineData(true, "Views/Main.axaml")]
    public void ConstructionAttachesMetadataBeforeInitialization(bool enabled, string path)
    {
        var xaml = "<t:SourceInfoProbe " + Ns + " Label='root'>\n  <t:SourceInfoProbe Label='child'/>\n</t:SourceInfoProbe>";
        foreach (var value in CompileBoth(xaml, enabled, path))
        {
            var root = Assert.IsType<SourceInfoProbe>(value);
            CheckInitialization(root, enabled ? Info(1, 2, path) : null);
            CheckInitialization(Assert.Single(root.Children), enabled ? Info(2, 4, path) : null);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PopulatePreservesExistingRootMetadata(bool enabled, bool existingInfo)
    {
        var xaml = "<t:SourceInfoProbe " + Ns + " Label='root'>\n  <t:SourceInfoProbe Label='child'/>\n</t:SourceInfoProbe>";
        var previous = existingInfo ? Info(12, 8, "/original.axaml") : null;
        for (var compiler = 0; compiler < 2; compiler++)
        {
            var root = new SourceInfoProbe();
            SourceInfo.SetXamlSourceInfo(root, previous);
            if (compiler == 0)
            {
                var result = AvaloniaUpstreamCompilation.Compile(xaml, enabled, Path, root);
                Assert.Null(result.Error);
                Assert.Same(root, result.Root);
            }
            else Assert.Same(root, AvaloniaCompilation.Build(xaml, enabled, Path, root));
            CheckInitialization(root, previous);
            CheckInitialization(Assert.Single(root.Children), enabled ? Info(2, 4) : null);
        }
    }

    [AvaloniaTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AvaloniaObjectsReceiveElementLocations(string newline)
    {
        var xaml = "<Border " + Ns + ">" + newline + "  <TextBlock Text='child'/>" + newline + "</Border>";
        foreach (var value in CompileBoth(xaml))
        {
            var root = Assert.IsType<Border>(value);
            Assert.Equal(Info(1, 2), SourceInfo.GetXamlSourceInfo(root));
            Assert.Equal(Info(2, 4), SourceInfo.GetXamlSourceInfo(root.Child!));
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceKeyLocationsExistBeforeLazyValuesAreCreated(bool property)
    {
        var entries = "\n  <x:String x:Key='text'>hello</x:String>\n  <x:Int32 x:Key='number'>7</x:Int32>\n  <t:SourceInfoProbe x:Key='object'/>\n  <t:SourceInfoExtension x:Key='provided'/>\n";
        var xaml = property ? "<Border " + Ns + "><Border.Resources>" + entries + "</Border.Resources></Border>" : Dictionary(entries);
        foreach (var value in CompileBoth(xaml))
        {
            var dictionary = value is Border border ? border.Resources : Assert.IsType<ResourceDictionary>(value);
            Assert.Equal(0, SourceInfoProbe.Constructions);
            Assert.Equal(Info(2, 26), SourceInfo.GetXamlSourceInfo(dictionary, "text"));
            Assert.Equal(Info(3, 27), SourceInfo.GetXamlSourceInfo(dictionary, "number"));
            Assert.Equal(Info(4, 4), SourceInfo.GetXamlSourceInfo(dictionary, "object"));
            Assert.Equal(Info(5, 4), SourceInfo.GetXamlSourceInfo(dictionary, "provided"));
            Assert.Null(SourceInfo.GetXamlSourceInfo(dictionary["text"]!));
            Assert.Null(SourceInfo.GetXamlSourceInfo(dictionary["number"]!));
            var constructed = Assert.IsType<SourceInfoProbe>(dictionary["object"]);
            Assert.Equal(Info(4, 4), constructed.BeginSource);
            Assert.Equal(Info(4, 4), constructed.EndSource);
            var provided = Assert.IsType<SourceInfoProbe>(dictionary["provided"]);
            Assert.Null(SourceInfo.GetXamlSourceInfo(provided));
            Assert.Equal(Info(5, 4), provided.ProviderSource);
            Assert.Equal(2, SourceInfoProbe.Constructions);
        }
    }

    [AvaloniaTheory]
    [InlineData("<x:String x:Key='item'>value</x:String>")]
    [InlineData("<x:String x:Key='item'>  value  </x:String>")]
    [InlineData("<x:String x:Key='item'><![CDATA[value]]></x:String>")]
    [InlineData("<x:String x:Key='item'>&amp;value</x:String>")]
    [InlineData("<t:SourceInfoParsed x:Key='item'>parsed</t:SourceInfoParsed>")]
    [InlineData("<t:SourceInfoConverted x:Key='item'>converted</t:SourceInfoConverted>")]
    [InlineData("<t:SourceInfoParsed x:Key='item'><x:String>parsed</x:String></t:SourceInfoParsed>")]
    [InlineData("<t:SourceInfoConverted x:Key='item'><x:String>converted</x:String></t:SourceInfoConverted>")]
    public void ConvertedAndIntrinsicResourcesPreserveTheirTransformedLocation(string entry)
    {
        var xaml = Dictionary("\n  " + entry + "\n");
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, true, Path);
        Assert.Null(baseline.Error);
        var expected = Assert.IsType<ResourceDictionary>(baseline.Root);
        var actual = Assert.IsType<ResourceDictionary>(AvaloniaCompilation.Build(xaml, true, Path));
        var location = SourceInfo.GetXamlSourceInfo(expected, "item");
        Assert.NotNull(location);
        Assert.Equal(2, location.LineNumber);
        Assert.Equal(location, SourceInfo.GetXamlSourceInfo(actual, "item"));
        Assert.Equal(SourceInfo.GetXamlSourceInfo(expected["item"]!), SourceInfo.GetXamlSourceInfo(actual["item"]!));
    }

    [AvaloniaFact]
    public void DisabledMetadataDoesNotRecordKeysOrObjects()
    {
        foreach (var value in CompileBoth(Dictionary("<t:SourceInfoProbe x:Key='item'/>"), false))
        {
            var dictionary = Assert.IsType<ResourceDictionary>(value);
            Assert.Null(SourceInfo.GetXamlSourceInfo(dictionary));
            Assert.Null(SourceInfo.GetXamlSourceInfo(dictionary, "item"));
            Assert.Null(SourceInfo.GetXamlSourceInfo(dictionary["item"]!));
        }
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData(" x:Shared='False'")]
    public void DeferredResourceInstancesRetainDefinitionLocations(string shared)
    {
        foreach (var value in CompileBoth(Dictionary("\n  <t:SourceInfoProbe x:Key='item'" + shared + "/>\n")))
        {
            var dictionary = Assert.IsType<ResourceDictionary>(value);
            var first = dictionary["item"]!;
            var second = dictionary["item"]!;
            Assert.Equal(Info(2, 4), SourceInfo.GetXamlSourceInfo(first));
            Assert.Equal(Info(2, 4), SourceInfo.GetXamlSourceInfo(second));
            Assert.Equal(shared.Length == 0, ReferenceEquals(first, second));
        }
    }

    [AvaloniaFact]
    public void DeferredTemplatesRetainTheTemplateDocumentLocation()
    {
        foreach (var value in CompileBoth("<DataTemplate " + Ns + ">\n  <TextBlock Text='deferred'/>\n</DataTemplate>"))
        {
            var template = Assert.IsType<DataTemplate>(value);
            Assert.Equal(Info(1, 2), SourceInfo.GetXamlSourceInfo(template));
            var first = template.Build(null)!;
            var second = template.Build(null)!;
            Assert.NotSame(first, second);
            Assert.Equal(Info(2, 4), SourceInfo.GetXamlSourceInfo(first));
            Assert.Equal(Info(2, 4), SourceInfo.GetXamlSourceInfo(second));
        }
    }

    [AvaloniaFact]
    public void NativeFactoryReturnedObjectsDoNotReceiveConstructionMetadata()
    {
        var xaml = "<t:SourceInfoProbe " + Ns + ">\n  <t:SourceInfoProbe x:FactoryMethod='Create' Label='factory'/>\n</t:SourceInfoProbe>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml, true, Path).Error);
        var value = AvaloniaCompilation.Build(xaml, true, Path);
        CheckInitialization(Assert.Single(Assert.IsType<SourceInfoProbe>(value).Children), null);
    }

    [AvaloniaTheory]
    [InlineData("<x:String x:Key='item'/>")]
    [InlineData("<x:Type x:Key='item' TypeName='TextBlock'/>")]
    [InlineData("<x:Static x:Key='item' Member='t:SourceInfoProbe.Shared'/>")]
    [InlineData("<x:Null x:Key='item'/>")]
    public void NativeIntrinsicResourceExtensionsKeepElementLocations(string entry)
    {
        var xaml = Dictionary("\n  " + entry + "\n");
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml, true, Path).Error);
        var dictionary = Assert.IsType<ResourceDictionary>(AvaloniaCompilation.Build(xaml, true, Path));
        Assert.Equal(Info(2, 4), SourceInfo.GetXamlSourceInfo(dictionary, "item"));
    }

    [AvaloniaFact]
    public void NativeDocumentsWithoutAPathHaveUnknownSourceUris()
    {
        var root = Assert.IsType<SourceInfoProbe>(AvaloniaCompilation.Build("<t:SourceInfoProbe " + Ns + " Label='root'/>", true, ""));
        CheckInitialization(root, Info(1, 2, ""));
    }

    [AvaloniaTheory]
    [InlineData("{t:SourceInfo}")]
    [InlineData("{t:SourceInfoWrapper Value={t:SourceInfo}}")]
    public void AttributeMarkupProvidersRetainTheirOwnLocation(string expression)
    {
        var xaml = "<t:SourceInfoCarrier " + Ns + "\n  Value='" + expression + "'/>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, true, Path);
        Assert.Null(baseline.Error);
        var expected = Assert.IsType<SourceInfoProbe>(Assert.IsType<SourceInfoCarrier>(baseline.Root).Value);
        var actual = Assert.IsType<SourceInfoProbe>(Assert.IsType<SourceInfoCarrier>(AvaloniaCompilation.Build(xaml, true, Path)).Value);
        Assert.NotNull(expected.ProviderSource);
        Assert.Equal(expected.ProviderSource, actual.ProviderSource);
        Assert.Null(SourceInfo.GetXamlSourceInfo(actual));
    }

    [AvaloniaTheory]
    [InlineData(false, "SourceInfoProbe")]
    [InlineData(true, "SourceInfoProbe")]
    [InlineData(false, "SourceInfoEagerExtension")]
    [InlineData(true, "SourceInfoEagerExtension")]
    public void ResourceMetadataReusesTheOriginalGetterAndProvidedKey(bool enabled, string type)
    {
        var xaml = "<t:SourceInfoResourceHost " + Ns + "><t:SourceInfoResourceHost.Resources>\n  <t:" + type + " x:Key='{t:SourceInfoKey}' Name='eager'/>\n</t:SourceInfoResourceHost.Resources></t:SourceInfoResourceHost>";
        foreach (var value in CompileBoth(xaml, enabled))
        {
            var root = Assert.IsType<SourceInfoResourceHost>(value);
            Assert.Equal(1, root.GetterCalls);
            Assert.Equal(1, SourceInfoKeyExtension.Calls);
            Assert.Equal(1, SourceInfoProbe.Constructions);
            Assert.Equal(new[] { "get", "key", "new" }, SourceInfoResourceHost.Events);
            var key = Assert.Single(root.Dictionary.Keys);
            Assert.Same(SourceInfoKeyExtension.LastKey, key);
            Assert.Equal(enabled ? Info(2, 4) : null, SourceInfo.GetXamlSourceInfo(root.Dictionary, key));
            var item = Assert.IsType<SourceInfoProbe>(root.Dictionary[key]);
            Assert.Equal(enabled ? Info(2, 4) : null, type == "SourceInfoProbe" ? SourceInfo.GetXamlSourceInfo(item) : item.ProviderSource);
        }
    }

    [AvaloniaFact]
    public void FailedResourceInsertionPreservesThePreviousLocation()
    {
        var xaml = "<t:SourceInfoResourceHost " + Ns + " Duplicate='True'><t:SourceInfoResourceHost.Resources>\n  <x:String x:Key='item'>new</x:String>\n</t:SourceInfoResourceHost.Resources></t:SourceInfoResourceHost>";
        for (var compiler = 0; compiler < 2; compiler++)
        {
            if (compiler == 0) Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml, true, Path).Error);
            else Assert.Throws<System.Reflection.TargetInvocationException>(() => AvaloniaCompilation.Build(xaml, true, Path));
            var dictionary = SourceInfoResourceHost.Last!.Dictionary;
            Assert.Equal("existing", dictionary["item"]);
            Assert.Equal(Info(8, 2, "/original.axaml"), SourceInfo.GetXamlSourceInfo(dictionary, "item"));
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCompiledMergesKeepImportedAndLocalLocations(bool notShared)
    {
        var project = new ResourceProjectFixture(new[]
        {
            ("Imported.axaml", Dictionary("\n  <x:String x:Key='imported'>first</x:String>\n  <x:String x:Key='local'>old</x:String>\n")),
            ("Main.axaml", Dictionary(ResourceProjectFixture.Include("Imported.axaml", "MergeResourceInclude") + "\n  <t:SourceInfoProbe x:Key='local'" + (notShared ? " x:Shared='False'" : "") + "/>\n"))
        }, createSourceInfo: true);
        var dictionary = Assert.IsType<ResourceDictionary>(project.Build("Main.axaml"));
        Assert.Equal("first", dictionary["imported"]);
        Assert.Equal(Info(2, 30, "Imported.axaml"), SourceInfo.GetXamlSourceInfo(dictionary, "imported"));
        Assert.Equal(Info(2, 4, "Main.axaml"), SourceInfo.GetXamlSourceInfo(dictionary, "local"));
        Assert.Equal(Info(2, 4, "Main.axaml"), SourceInfo.GetXamlSourceInfo(dictionary["local"]!));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeReplacementWithoutMetadataClearsTheReplacedKeyLocation(bool local)
    {
        var target = new ResourceDictionary { ["item"] = "old" };
        SourceInfo.SetXamlSourceInfo(target, "item", Info(1, 2));
        if (local) AvaloniaCompiledResourceOperations.SetResource(target, "item", "new");
        else AvaloniaCompiledResourceOperations.Merge(target, new ResourceDictionary { ["item"] = "new" });
        Assert.Equal("new", target["item"]);
        Assert.Null(SourceInfo.GetXamlSourceInfo(target, "item"));
    }

    [AvaloniaFact]
    public void NativeSessionDisposalKeepsFrameworkSourceMetadata()
    {
        var root = AvaloniaCompilation.Build("<Border " + Ns + "/>", true, Path);
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        session!.Dispose();
        Assert.Equal(Info(1, 2), SourceInfo.GetXamlSourceInfo(root));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCodeBehindFactoriesAttachRootMetadataAfterTheConstructor(bool initializeInConstructor)
    {
        var code = """
            using Avalonia.Controls;
            using Avalonia.Markup.Xaml.Diagnostics;
            namespace SourceMetadata;
            public partial class View : Border
            {
                public static XamlSourceInfo? ConstructorInfo;
                public XamlSourceInfo? SetterInfo;
                public View() { ConstructorInfo = XamlSourceInfo.GetXamlSourceInfo(this); INITIALIZE }
                public string Label { get => ""; set => SetterInfo = XamlSourceInfo.GetXamlSourceInfo(this); }
            }
            """.Replace("INITIALIZE", initializeInConstructor ? "InitializeComponent();" : "", StringComparison.Ordinal);
        var xaml = "<Border " + Ns + " x:Class='SourceMetadata.View' Label='root'/>";
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", xaml) }, sourceCode: code, createSourceInfo: true);
        var root = fixture.Build("View.axaml");
        Assert.Null(root.GetType().GetField("ConstructorInfo")!.GetValue(null));
        Assert.Equal(Info(1, 2, "View.axaml"), SourceInfo.GetXamlSourceInfo(root));
        Assert.Equal(initializeInConstructor ? null : Info(1, 2, "View.axaml"), root.GetType().GetField("SetterInfo")!.GetValue(root));
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData(null, null, "Debug", true)]
    [InlineData(null, null, "Release", false)]
    [InlineData(null, "false", "Debug", false)]
    [InlineData(null, "true", "Release", true)]
    [InlineData("false", "true", "Debug", false)]
    [InlineData("true", "false", "Release", true)]
    public void BuildDefaultsHonorConfigurationAndExplicitOverrides(string? xamlg, string? avalonia, string? configuration, bool expected)
    {
        var options = new Dictionary<string, string>();
        if (xamlg != null) options["build_property.XamlGCreateSourceInfo"] = xamlg;
        if (avalonia != null) options["build_property.AvaloniaXamlCreateSourceInfo"] = avalonia;
        if (configuration != null) options["build_property.Configuration"] = configuration;
        Assert.Equal(expected, AvaloniaBuildOptions.ReadCreateSourceInfo(new Options(options)));
    }

    private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }

    private static SourceInfo Info(int line, int column, string path = Path) => new(line, column, path.Length == 0 ? null : path);
    private static string Dictionary(string entries) => "<ResourceDictionary " + Ns + ">" + entries + "</ResourceDictionary>";
    private static void CheckInitialization(SourceInfoProbe probe, SourceInfo? expected)
    {
        Assert.Null(probe.ConstructorSource);
        Assert.Equal(expected, SourceInfo.GetXamlSourceInfo(probe));
        Assert.Equal(expected, probe.BeginSource);
        Assert.Equal(expected, probe.SetterSource);
        Assert.Equal(expected, probe.EndSource);
    }

    private static IEnumerable<object> CompileBoth(string xaml, bool enabled = true, string path = Path)
    {
        SourceInfoProbe.Constructions = SourceInfoKeyExtension.Calls = 0;
        SourceInfoResourceHost.Events.Clear();
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, enabled, path.Length == 0 ? null : path);
        Assert.Null(baseline.Error);
        yield return baseline.Root!;
        SourceInfoProbe.Constructions = SourceInfoKeyExtension.Calls = 0;
        SourceInfoResourceHost.Events.Clear();
        yield return AvaloniaCompilation.Build(xaml, enabled, path);
    }
}

public sealed class SourceInfoProbe : ISupportInitialize, INamed
{
    public SourceInfoProbe() { Constructions++; SourceInfoResourceHost.Events.Add("new"); ConstructorSource = SourceInfo.GetXamlSourceInfo(this); }
    public static int Constructions { get; set; }
    public static object Shared { get; } = new();
    public static SourceInfoProbe Create() => new();
    public SourceInfo? ConstructorSource { get; }
    public SourceInfo? BeginSource { get; private set; }
    public SourceInfo? SetterSource { get; private set; }
    public SourceInfo? EndSource { get; private set; }
    public SourceInfo? ProviderSource { get; set; }
    public string? Name { get; set; }
    [Content] public List<SourceInfoProbe> Children { get; } = new();
    public string? Label { get => null; set => SetterSource = SourceInfo.GetXamlSourceInfo(this); }
    public void BeginInit() => BeginSource = SourceInfo.GetXamlSourceInfo(this);
    public void EndInit() => EndSource = SourceInfo.GetXamlSourceInfo(this);
}

public sealed class SourceInfoExtension
{
    public SourceInfoProbe ProvideValue() => new() { ProviderSource = SourceInfo.GetXamlSourceInfo(this) };
}

public sealed class SourceInfoEagerExtension : INamed
{
    public string? Name { get; set; }
    public object ProvideValue() => new SourceInfoProbe { ProviderSource = SourceInfo.GetXamlSourceInfo(this) };
}

public sealed class SourceInfoWrapperExtension
{
    public object? Value { get; set; }
    public object? ProvideValue() => Value;
}

public sealed class SourceInfoCarrier
{
    public object? Value { get; set; }
}

public sealed class SourceInfoParsed
{
    public static SourceInfoParsed Parse(string _) => new();
}

[TypeConverter(typeof(SourceInfoConverter))]
public sealed class SourceInfoConverted;

public sealed class SourceInfoConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => new SourceInfoConverted();
}

public sealed class SourceInfoResourceHost
{
    public SourceInfoResourceHost() => Last = this;
    public static SourceInfoResourceHost? Last { get; private set; }
    public static List<string> Events { get; } = new();
    public ResourceDictionary Dictionary { get; } = new();
    public int GetterCalls { get; private set; }
    public IResourceDictionary Resources { get { GetterCalls++; Events.Add("get"); return Dictionary; } }
    public bool Duplicate
    {
        get => false;
        set
        {
            if (!value) return;
            Dictionary.Add("item", "existing");
            SourceInfo.SetXamlSourceInfo(Dictionary, "item", new(8, 2, "/original.axaml"));
        }
    }
}

public sealed class SourceInfoKeyExtension
{
    public static int Calls { get; set; }
    public static object? LastKey { get; private set; }
    public object ProvideValue() { Calls++; SourceInfoResourceHost.Events.Add("key"); return LastKey = new object(); }
}
