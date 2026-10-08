using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ParentStackProtocolTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("tree", 0)]
    [InlineData("tree", 1)]
    [InlineData("tree", 2)]
    [InlineData("template", 0)]
    [InlineData("template", 1)]
    [InlineData("template", 2)]
    [InlineData("resource", 0)]
    [InlineData("resource", 1)]
    [InlineData("resource", 2)]
    public void EagerAndLazyParentsPreserveLocalAndExternalOrder(string scope, int external)
    {
        var xaml = scope switch
        {
            "tree" => "<StackPanel " + Ns + "><Border Tag='{t:ParentProtocolProbe}'/></StackPanel>",
            "template" => "<ControlTemplate " + Ns + " TargetType='Button'><Border Tag='{t:ParentProtocolProbe}'/></ControlTemplate>",
            _ => "<ResourceDictionary " + Ns + "><t:ParentProtocolProbe x:Key='snapshot'/></ResourceDictionary>"
        };
        IServiceProvider? services = external switch { 1 => new LazyProtocolParents(), 2 => new EagerProtocolParents(), _ => null };
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, services: services);
        Assert.Null(baseline.Error);
        var expected = Snapshot(baseline.Root!);
        Assert.True(expected.IsEager);
        Assert.True(expected.StableDirectParents);
        Assert.Equal(expected.Parents, expected.ChainedParents);

        var fixture = new ResourceProjectFixture(new[] { ("Parents.axaml", xaml) });
        var actual = Snapshot(fixture.Build("Parents.axaml", services));
        Assert.True(actual.IsEager);
        Assert.True(actual.StableDirectParents);
        Assert.Equal(expected.Parents, actual.Parents);
        Assert.Equal(expected.DirectParents, actual.DirectParents);
        Assert.Equal(expected.ChainedParents, actual.ChainedParents);
    }

    private static ParentProtocolSnapshot Snapshot(object root) => root switch
    {
        StackPanel panel => Assert.IsType<ParentProtocolSnapshot>(Assert.IsType<Border>(Assert.Single(panel.Children)).Tag),
        IControlTemplate template => Assert.IsType<ParentProtocolSnapshot>(Assert.IsType<Border>(template.Build(new Button())!.Result).Tag),
        ResourceDictionary dictionary => Assert.IsType<ParentProtocolSnapshot>(dictionary["snapshot"]),
        _ => throw new InvalidOperationException("Unexpected parent-protocol fixture root.")
    };
}

public sealed record ParentProtocolSnapshot(bool IsEager, bool StableDirectParents, string[] Parents, string[] DirectParents, string[] ChainedParents);

public sealed class ParentProtocolProbe
{
    public ParentProtocolSnapshot ProvideValue(IServiceProvider services)
    {
        var provider = (IAvaloniaXamlIlParentStackProvider)services.GetService(typeof(IAvaloniaXamlIlParentStackProvider))!;
        var eager = provider as IAvaloniaXamlIlEagerParentStackProvider;
        var chain = new List<object>();
        var depth = 0;
        for (var current = eager; current != null; current = current.ParentProvider)
        {
            if (++depth > 128) throw new InvalidOperationException("Parent-provider cycle.");
            var parents = current.DirectParentsStack;
            for (var index = parents.Count - 1; index >= 0; index--) chain.Add(parents[index]);
        }
        static string Name(object value) => value is ProtocolParentMarker marker ? marker.Name : value.GetType().FullName!;
        return new(eager != null, eager != null && ReferenceEquals(eager.DirectParentsStack, eager.DirectParentsStack),
            provider.Parents.Select(Name).ToArray(), eager?.DirectParentsStack.Select(Name).ToArray() ?? Array.Empty<string>(), chain.Select(Name).ToArray());
    }
}

public sealed record ProtocolParentMarker(string Name);

public class LazyProtocolParents : IServiceProvider, IAvaloniaXamlIlParentStackProvider
{
    public IEnumerable<object> Parents { get; } = new object[] { new ProtocolParentMarker("external-near"), new ProtocolParentMarker("external-far") };
    public object? GetService(Type serviceType) => serviceType == typeof(IAvaloniaXamlIlParentStackProvider) ? this : null;
}

public sealed class EagerProtocolParents : LazyProtocolParents, IAvaloniaXamlIlEagerParentStackProvider
{
    public EagerProtocolParents() => DirectParentsStack = Parents.Reverse().ToArray();
    public IReadOnlyList<object> DirectParentsStack { get; }
    public IAvaloniaXamlIlEagerParentStackProvider? ParentProvider => null;
}
