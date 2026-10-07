using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RemainingResourceTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(2, true)]
    public void ResourceCapacityReadsTheGetterBeforeTheAssignmentGroup(int count, bool deferred)
    {
        var entries = string.Concat(Enumerable.Range(0, count).Select(index => deferred ? "<Border x:Key='" + index + "'/>" : "<x:String x:Key='" + index + "'>value</x:String>"));
        var xaml = "<t:ResourceCapacityHost " + Ns + " Marker='assigned'><t:ResourceCapacityHost.Resources>" + entries + "</t:ResourceCapacityHost.Resources></t:ResourceCapacityHost>";
        string[]? expected = null;
        foreach (var native in new[] { false, true })
        {
            ResourceCapacityHost.Events.Clear();
            object? root;
            if (native) root = new ResourceProjectFixture(new[] { ("Resources.axaml", xaml) }).Build("Resources.axaml");
            else
            {
                var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
                Assert.Null(baseline.Error); root = baseline.Root;
            }
            Assert.IsType<ResourceCapacityHost>(root);
            var actual = ResourceCapacityHost.Events.ToArray();
            if (expected == null) expected = actual;
            else Assert.Equal(expected, actual);
            Assert.Equal(count >= 2 ? "get:0" : "set", actual[0]);
        }
    }

    [AvaloniaFact]
    public void InitOnlyCapacitySettersExecuteBeforeListItems()
    {
        var xaml = "<t:InitCapacityHost " + Ns + " Values='1,2,3'/>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        foreach (var root in new[] { Assert.IsType<InitCapacityHost>(baseline.Root), Assert.IsType<InitCapacityHost>(AvaloniaCompilation.Build(xaml)) })
        {
            Assert.Equal(new[] { 1, 2, 3 }, root.Values);
            Assert.Equal(new[] { 3, 3, 3 }, root.Values!.Capacities);
        }
    }
}

public sealed class ResourceCapacityHost : Control
{
    public static List<string> Events { get; } = new();
    private readonly ResourceDictionary _resources = new();
    public string Marker { set => Events.Add("set"); }
    public new IResourceDictionary Resources { get { Events.Add("get:" + _resources.Count); return _resources; } }
}

public sealed class InitCapacityHost
{
    public InitCapacityList? Values { get; set; }
}

public sealed class InitCapacityList : AvaloniaList<int>
{
    public new int Capacity { get => base.Capacity; init => base.Capacity = value; }
    public List<int> Capacities { get; } = new();
    public override void Add(int item) { Capacities.Add(Capacity); base.Add(item); }
}
