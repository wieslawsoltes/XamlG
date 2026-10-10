using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DeferredLiteralShapeTests
{
    private const string Xaml = """
        <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
            xmlns:t="clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests">
            <t:DeferredLiteralOwner x:Key="first" Color="Red" Margin="1,2,3,4" Number="11"/>
            <t:DeferredLiteralOwner x:Key="second" Color="Blue" Margin="5,6,7,8" Number="22"/>
        </ResourceDictionary>
        """;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoweredValuesKeepLazyConstructionDistinctSourcesAndEditing(bool sourceInfo)
    {
        DeferredLiteralOwner.Events.Clear();
        DeferredLiteralOwner.Failure = null;
        var fixture = new ResourceProjectFixture(new[] { ("Literals.axaml", Xaml) }, createSourceInfo: sourceInfo);
        var dictionary = Assert.IsType<ResourceDictionary>(fixture.Build("Literals.axaml"));
        Assert.Empty(DeferredLiteralOwner.Events);
        var output = fixture.Result.Documents.Single().Output;
        var first = Assert.IsType<DeferredLiteralOwner>(dictionary["first"]);
        var second = Assert.IsType<DeferredLiteralOwner>(dictionary["second"]);
        Assert.NotSame(first, second);
        Assert.Same(first, dictionary["first"]);
        Assert.Equal(Colors.Red, first.Color);
        Assert.Equal(Colors.Blue, second.Color);
        Assert.Equal(new Thickness(1, 2, 3, 4), first.Margin);
        Assert.Equal(new Thickness(5, 6, 7, 8), second.Margin);
        Assert.Equal(11, first.Number);
        Assert.Equal(22, second.Number);
        Assert.Equal(new[] { "new", "begin", "color", "margin", "number:11", "end",
            "new", "begin", "color", "margin", "number:22", "end" }, DeferredLiteralOwner.Events);
        Assert.True(XamlRuntimeSession.TryGet(first, out var firstSession));
        Assert.True(XamlRuntimeSession.TryGet(second, out var secondSession));
        var firstNode = firstSession!.FindNode(first)!;
        var secondNode = secondSession!.FindNode(second)!;
        Assert.NotEqual(firstNode.Key, secondNode.Key);
        Assert.NotEqual(firstNode.Source!.Start, secondNode.Source!.Start);
        Assert.True(firstSession.Apply(0, new[] { new XamlPropertyUpdate(firstNode.Key, "Color", Colors.Green) }).Applied);
        Assert.Equal(Colors.Green, first.Color);
        Assert.Equal(Colors.Blue, second.Color);
        if (!sourceInfo)
            foreach (var literal in new[] { "Red", "Blue", "11", "22" })
                Assert.Contains(output.SourceMappings, mapping => mapping.GeneratedSpan.Length > 0 &&
                    Xaml.Substring(mapping.SourceSpan.Start, mapping.SourceSpan.Length).Contains(literal, StringComparison.Ordinal));
        Assert.True(XamlRuntimeSession.TryGet(dictionary, out var owner));
        owner!.Dispose();
        Assert.Equal(2, DeferredLiteralOwner.Events.Count(value => value == "dispose"));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticValuesAreEvaluatedOnEachDeferredInvocation(bool sourceInfo)
    {
        const string xaml = """
            <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:t="clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests">
                <t:DeferredLiteralOwner x:Key="first" Color="{x:Static t:DeferredLiteralOwner.ReadColor}"/>
                <t:DeferredLiteralOwner x:Key="second" Color="{x:Static t:DeferredLiteralOwner.ReadColor}"/>
                <t:DeferredLiteralOwner x:Key="failure" Color="{x:Static t:DeferredLiteralOwner.ReadColor}"/>
            </ResourceDictionary>
            """;
        DeferredLiteralOwner.Events.Clear();
        DeferredLiteralOwner.Failure = null;
        var fixture = new ResourceProjectFixture(new[] { ("Static.axaml", xaml) }, createSourceInfo: sourceInfo);
        var dictionary = Assert.IsType<ResourceDictionary>(fixture.Build("Static.axaml"));
        Assert.Empty(DeferredLiteralOwner.Events);
        DeferredLiteralOwner.CurrentColor = Colors.Red;
        var first = Assert.IsType<DeferredLiteralOwner>(dictionary["first"]);
        DeferredLiteralOwner.CurrentColor = Colors.Blue;
        var second = Assert.IsType<DeferredLiteralOwner>(dictionary["second"]);
        Assert.Equal(Colors.Red, first.Color);
        Assert.Equal(Colors.Blue, second.Color);
        Assert.Equal(new[] { "new", "begin", "static", "color", "end", "new", "begin", "static", "color", "end" },
            DeferredLiteralOwner.Events);
        DeferredLiteralOwner.Events.Clear();
        DeferredLiteralOwner.Failure = "static";
        try
        {
            var error = Record.Exception(() => _ = dictionary["failure"]);
            Assert.NotNull(error);
            Assert.Equal("failed:static", error.GetBaseException().Message);
            Assert.Equal(new[] { "new", "begin", "static", "dispose" }, DeferredLiteralOwner.Events);
        }
        finally
        {
            DeferredLiteralOwner.Failure = null;
            Assert.True(XamlRuntimeSession.TryGet(dictionary, out var owner));
            owner!.Dispose();
        }
        Assert.Equal(3, DeferredLiteralOwner.Events.Count(value => value == "dispose"));
    }

    [AvaloniaTheory]
    [InlineData(false, "begin")]
    [InlineData(true, "begin")]
    [InlineData(false, "color")]
    [InlineData(true, "color")]
    [InlineData(false, "margin")]
    [InlineData(true, "margin")]
    [InlineData(false, "end")]
    [InlineData(true, "end")]
    public void LoweredValuesRetainFailureOrderAndDisposeOwnedConstruction(bool sourceInfo, string failure)
    {
        DeferredLiteralOwner.Events.Clear();
        DeferredLiteralOwner.Failure = null;
        var fixture = new ResourceProjectFixture(new[] { ("Failure.axaml", Xaml) }, createSourceInfo: sourceInfo);
        var dictionary = Assert.IsType<ResourceDictionary>(fixture.Build("Failure.axaml"));
        DeferredLiteralOwner.Failure = failure;
        try
        {
            var error = Record.Exception(() => _ = dictionary["first"]);
            Assert.NotNull(error);
            Assert.Equal("failed:" + failure, error.GetBaseException().Message);
            var expected = new[] { "new", "begin", "color", "margin", "number:11", "end" };
            Assert.Equal(expected.Take(Array.IndexOf(expected, failure) + 1).Append("dispose"), DeferredLiteralOwner.Events);
        }
        finally
        {
            DeferredLiteralOwner.Failure = null;
            Assert.True(XamlRuntimeSession.TryGet(dictionary, out var owner));
            owner!.Dispose();
        }
        Assert.Equal(1, DeferredLiteralOwner.Events.Count(value => value == "dispose"));
    }
}

public sealed class DeferredLiteralOwner : ISupportInitialize
{
    public static List<string> Events { get; } = new();
    public static string? Failure { get; set; }
    public static Color CurrentColor { get; set; }
    public static Color ReadColor { get { Step("static"); return CurrentColor; } }
    private Color _color;
    private Thickness _margin;
    private int _number;

    public DeferredLiteralOwner()
    {
        Events.Add("new");
        var session = new XamlRuntimeSession();
        session.TrackCleanup(() => Events.Add("dispose"));
        session.Attach(this);
    }

    public Color Color { get => _color; set { Step("color"); _color = value; } }
    public Thickness Margin { get => _margin; set { Step("margin"); _margin = value; } }
    public int Number { get => _number; set { Step("number:" + value); _number = value; } }
    public void BeginInit() => Step("begin");
    public void EndInit() => Step("end");
    private static void Step(string name)
    {
        Events.Add(name);
        if (Failure == name) throw new InvalidOperationException("failed:" + name);
    }
}
