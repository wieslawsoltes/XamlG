using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DeferredConstructionShapeTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredShapesPreserveIndependentFramesEditingServicesAndFailureCleanup(bool sourceInfo)
    {
        DeferredShapeExtension.Events.Clear();
        const string xaml = """
            <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:t="clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests">
                <t:DeferredShapeExtension x:Key="first" Label="alpha"/>
                <t:DeferredShapeExtension x:Key="second" Label="beta"/>
                <t:DeferredShapeExtension x:Key="failure" Label="fail"/>
            </ResourceDictionary>
            """;
        var fixture = new ResourceProjectFixture(new[] { ("Shapes.axaml", xaml) }, createSourceInfo: sourceInfo);
        var root = Assert.IsType<ResourceDictionary>(fixture.Build("Shapes.axaml"));
        Assert.Empty(DeferredShapeExtension.Events);
        var first = Assert.IsType<DeferredShapeExtension>(root["first"]);
        var second = Assert.IsType<DeferredShapeExtension>(root["second"]);
        Assert.NotSame(first, second);
        Assert.Same(first, root["first"]);
        Assert.Same(root, first.Root);
        Assert.Same(root, second.Root);
        Assert.Equal(1, first.DictionaryParents);
        Assert.Equal(1, second.DictionaryParents);
        Assert.Equal(new[] { "new", "begin", "set:alpha", "end", "provide:alpha", "new", "begin", "set:beta", "end", "provide:beta" }, DeferredShapeExtension.Events);
        Assert.True(XamlRuntimeSession.TryGet(first, out var session));
        var node = session!.FindNode(first)!;
        Assert.NotNull(node.Source);
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate(node.Key, "Label", "changed") }).Applied);
        Assert.Equal("changed", first.Label);
        Assert.Equal("beta", second.Label);
        var failure = Record.Exception(() => _ = root["failure"]);
        Assert.NotNull(failure);
        Assert.Contains("initialization failed", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("dispose:fail", DeferredShapeExtension.Events);
        Assert.True(XamlRuntimeSession.TryGet(root, out var owner));
        owner!.Dispose();
        Assert.Contains("dispose:changed", DeferredShapeExtension.Events);
        Assert.Contains("dispose:beta", DeferredShapeExtension.Events);
    }
}

public sealed class DeferredShapeExtension : ISupportInitialize
{
    public static List<string> Events { get; } = new();
    private string? _label;
    public DeferredShapeExtension()
    {
        Events.Add("new");
        var owner = new XamlRuntimeSession();
        owner.TrackCleanup(() => Events.Add("dispose:" + _label));
        owner.Attach(this);
    }
    public string? Label { get => _label; set { Events.Add("set:" + value); _label = value; } }
    public object? Root { get; private set; }
    public int DictionaryParents { get; private set; }
    public void BeginInit() => Events.Add("begin");
    public void EndInit()
    {
        Events.Add("end");
        if (Label == "fail") throw new InvalidOperationException("initialization failed");
    }
    public object ProvideValue(IServiceProvider services)
    {
        Events.Add("provide:" + Label);
        Root = ((IRootObjectProvider)services.GetService(typeof(IRootObjectProvider))!).RootObject;
        DictionaryParents = ((IAvaloniaXamlIlParentStackProvider)services.GetService(typeof(IAvaloniaXamlIlParentStackProvider))!).Parents.OfType<ResourceDictionary>().Count();
        return this;
    }
}
