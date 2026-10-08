using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RuntimeServiceContractTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("tree")]
    [InlineData("template")]
    [InlineData("resource")]
    public void TypeDescriptorContextUsesTheCompiledFrameworkContract(string scope)
    {
        var xaml = scope switch
        {
            "tree" => "<Border " + Ns + " Tag='{t:DescriptorProtocolProbe}'/>",
            "template" => "<ControlTemplate " + Ns + " TargetType='Button'><Border Tag='{t:DescriptorProtocolProbe}'/></ControlTemplate>",
            _ => "<ResourceDictionary " + Ns + "><t:DescriptorProtocolProbe x:Key='snapshot'/></ResourceDictionary>"
        };
        static object? Snapshot(object root) => root switch
        {
            Border border => border.Tag,
            IControlTemplate template => ((Border)template.Build(new Button())!.Result).Tag,
            ResourceDictionary dictionary => dictionary["snapshot"],
            _ => throw new InvalidOperationException()
        };
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        var expected = Snapshot(baseline.Root!);
        Assert.Equal("True:True:True:True:unsupported:unsupported", expected);
        var fixture = new ResourceProjectFixture(new[] { ("Services.axaml", xaml) });
        Assert.Equal(expected, Snapshot(fixture.Build("Services.axaml")));
    }

    [AvaloniaFact]
    public void DocumentRootSupersedesExternalRootsAfterConstruction()
    {
        var xaml = "<Border " + Ns + " Tag='{t:FrameworkRootProbe}'/>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, services: new FrameworkOuterRoot());
        Assert.Null(baseline.Error);
        Assert.Same(baseline.Root, ((Border)baseline.Root!).Tag);
        var fixture = new ResourceProjectFixture(new[] { ("Root.axaml", xaml) });
        var root = (Border)fixture.Build("Root.axaml", new FrameworkOuterRoot());
        Assert.Same(root, root.Tag);
    }

    [AvaloniaFact]
    public void CodeBehindClassNamesDoNotImplicitlyRequireMarkupProviders()
    {
        var xaml = "<Border " + Ns + " x:Class='XamlG.Avalonia.Tests.PlainComponentExtension' Tag='value'/>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, rootInstance: new PlainComponentExtension());
        Assert.Null(baseline.Error);
        Assert.Equal("value", Assert.IsType<PlainComponentExtension>(baseline.Root).Tag);
        var fixture = new ResourceProjectFixture(new[] { ("Component.axaml", xaml) });
        Assert.Equal("value", Assert.IsType<PlainComponentExtension>(fixture.Build("Component.axaml")).Tag);
    }
}

public sealed class PlainComponentExtension : Border { }

public sealed class DescriptorProtocolProbe
{
    public string ProvideValue(IServiceProvider services)
    {
        var context = (ITypeDescriptorContext)services;
        static string Call(Action action)
        {
            try { action(); return "supported"; }
            catch (NotSupportedException) { return "unsupported"; }
        }
        return $"{context.Container == null}:{context.Instance == null}:{context.PropertyDescriptor == null}:" +
            $"{ReferenceEquals(context, services.GetService(typeof(ITypeDescriptorContext)))}:" +
            Call(() => context.OnComponentChanging()) + ":" + Call(context.OnComponentChanged);
    }
}
public sealed class FrameworkRootProbe
{
    public object ProvideValue(IServiceProvider services) => ((IRootObjectProvider)services.GetService(typeof(IRootObjectProvider))!).RootObject;
}
public sealed class FrameworkOuterRoot : IServiceProvider, IRootObjectProvider
{
    public object RootObject { get; } = new();
    public object IntermediateRootObject => RootObject;
    public object? GetService(Type serviceType) => serviceType == typeof(IRootObjectProvider) ? this : null;
}
