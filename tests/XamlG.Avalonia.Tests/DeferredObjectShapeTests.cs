using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DeferredObjectShapeTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedObjectBodiesKeepInitializationParentsSourceEditingAndFailureCleanup(bool sourceInfo)
    {
        DeferredShapeExtension.Events.Clear();
        const string xaml = """
            <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:t="clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests">
                <t:DeferredShapeOwner x:Key="first" Value="{t:DeferredShape Label=alpha}"/>
                <t:DeferredShapeOwner x:Key="second" Value="{t:DeferredShape Label=beta}"/>
                <t:DeferredShapeOwner x:Key="failure" Value="{t:DeferredShape Label=fail}"/>
            </ResourceDictionary>
            """;
        var fixture = new ResourceProjectFixture(new[] { ("Objects.axaml", xaml) }, createSourceInfo: sourceInfo);
        var dictionary = Assert.IsType<ResourceDictionary>(fixture.Build("Objects.axaml"));
        if (!sourceInfo)
        {
            var output = fixture.Result.Documents.Single().Output;
            Assert.Contains("__XamlGBuildObject_", output.Source, StringComparison.Ordinal);
            foreach (var literal in new[] { "alpha", "beta", "fail" })
                Assert.Contains(output.SourceMappings, mapping =>
                    output.Source.Substring(mapping.GeneratedSpan.Start, mapping.GeneratedSpan.Length) == "\"" + literal + "\"" &&
                    xaml.Substring(mapping.SourceSpan.Start, mapping.SourceSpan.Length).Contains(literal, StringComparison.Ordinal));
        }
        Assert.Empty(DeferredShapeExtension.Events);
        var first = Assert.IsType<DeferredShapeOwner>(dictionary["first"]);
        var second = Assert.IsType<DeferredShapeOwner>(dictionary["second"]);
        var firstValue = Assert.IsType<DeferredShapeExtension>(first.Value);
        var secondValue = Assert.IsType<DeferredShapeExtension>(second.Value);
        Assert.NotSame(first, second); Assert.NotSame(firstValue, secondValue);
        Assert.Same(first, dictionary["first"]);
        Assert.Same(dictionary, firstValue.Root); Assert.Same(dictionary, secondValue.Root);
        Assert.Equal(1, firstValue.DictionaryParents); Assert.Equal(1, secondValue.DictionaryParents);
        Assert.Equal(new[] { "owner:new", "owner:begin", "new", "begin", "set:alpha", "end", "provide:alpha", "owner:set", "owner:end",
            "owner:new", "owner:begin", "new", "begin", "set:beta", "end", "provide:beta", "owner:set", "owner:end" }, DeferredShapeExtension.Events);
        Assert.True(XamlRuntimeSession.TryGet(first, out var session));
        var parent = session!.FindNode(first)!;
        var child = session.FindNode(firstValue)!;
        Assert.Equal(parent.Key, child.ParentKey);
        Assert.NotNull(parent.Source); Assert.NotNull(child.Source);
        Assert.True(XamlRuntimeSession.TryGet(second, out var otherSession));
        var other = otherSession!.FindNode(secondValue)!;
        Assert.NotEqual(child.Key, other.Key);
        Assert.NotEqual(child.Source.Start, other.Source!.Start);
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate(child.Key, "Label", "edited") }).Applied);
        Assert.Equal("edited", firstValue.Label); Assert.Equal("beta", secondValue.Label);
        DeferredShapeExtension.Events.Clear();
        var failure = Record.Exception(() => _ = dictionary["failure"]);
        Assert.NotNull(failure); Assert.Contains("initialization failed", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("dispose:fail", DeferredShapeExtension.Events);
        Assert.Contains("owner:dispose", DeferredShapeExtension.Events);
        Assert.DoesNotContain("owner:end", DeferredShapeExtension.Events);
        Assert.True(XamlRuntimeSession.TryGet(dictionary, out var owner)); owner!.Dispose();
        Assert.Contains("dispose:edited", DeferredShapeExtension.Events);
        Assert.Contains("dispose:beta", DeferredShapeExtension.Events);
    }

    [AvaloniaFact]
    public void SharedBrushBodiesKeepIndependentDynamicSubscriptions()
    {
        var xaml = ResourceProjectFixture.Dictionary("""
            <Color x:Key="firstColor">Red</Color><Color x:Key="secondColor">Blue</Color>
            <SolidColorBrush x:Key="first" Color="{DynamicResource firstColor}"/>
            <SolidColorBrush x:Key="second" Color="{DynamicResource secondColor}"/>
            """);
        var fixture = new ResourceProjectFixture(new[] { ("Brushes.axaml", xaml) });
        var dictionary = Assert.IsType<ResourceDictionary>(fixture.Build("Brushes.axaml"));
        var anchor = new Border { Resources = dictionary };
        var first = Assert.IsType<SolidColorBrush>(dictionary["first"]);
        var second = Assert.IsType<SolidColorBrush>(dictionary["second"]);
        Assert.Contains("__XamlGBuildObject_", fixture.Result.Documents.Single().Output.Source, StringComparison.Ordinal);
        Assert.Equal(Colors.Red, first.Color); Assert.Equal(Colors.Blue, second.Color);
        dictionary["firstColor"] = Colors.Green; Dispatcher.UIThread.RunJobs();
        Assert.Equal(Colors.Green, first.Color); Assert.Equal(Colors.Blue, second.Color);
        Assert.True(XamlRuntimeSession.TryGet(dictionary, out var owner)); owner!.Dispose();
        dictionary["firstColor"] = Colors.Yellow; dictionary["secondColor"] = Colors.Purple;
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(Colors.Yellow, first.Color); Assert.NotEqual(Colors.Purple, second.Color);
        GC.KeepAlive(anchor);
    }
}

public sealed class DeferredShapeOwner : AvaloniaObject, ISupportInitialize
{
    public static readonly StyledProperty<object?> ValueProperty = AvaloniaProperty.Register<DeferredShapeOwner, object?>(nameof(Value));
    public DeferredShapeOwner()
    {
        DeferredShapeExtension.Events.Add("owner:new");
        var owner = new XamlRuntimeSession();
        owner.TrackCleanup(() => DeferredShapeExtension.Events.Add("owner:dispose"));
        owner.Attach(this);
    }
    public object? Value { get => GetValue(ValueProperty); set { DeferredShapeExtension.Events.Add("owner:set"); SetValue(ValueProperty, value); } }
    public void BeginInit() => DeferredShapeExtension.Events.Add("owner:begin");
    public void EndInit() => DeferredShapeExtension.Events.Add("owner:end");
}
