using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class InheritedResourceFactoryTests
{
    private const string Code = """
        using System;
        using Avalonia.Controls;
        using XamlG.Runtime;
        namespace Resources {
          public class Observed {
            public static int Disposals;
            public object Payload;
            public Observed(IServiceProvider services) {
              Payload = services.GetService(typeof(string));
              ((XamlRuntimeContext)services.GetService(typeof(XamlRuntimeContext))).Session.TrackCleanup(() => Disposals++);
            }
          }
          public partial class BaseTheme : ResourceDictionary {
            public BaseTheme() { InitializeComponent(); }
          }
          public partial class Theme : BaseTheme {
            public Theme() { CONSTRUCTOR }
            public void Reinitialize() => InitializeComponent();
          }
        }
        """;
    private sealed class Services : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(string) ? "factory services" : null;
    }
    private static ResourceProjectFixture Project(string constructor = "")
    {
        const string ns = ResourceProjectFixture.Namespace + " xmlns:local='clr-namespace:Resources'";
        return new(new[]
        {
            ("Base.axaml", "<ResourceDictionary " + ns + " x:Class='Resources.BaseTheme'><local:Observed x:Key='base'/></ResourceDictionary>"),
            ("Theme.axaml", "<local:BaseTheme " + ns + " x:Class='Resources.Theme'><local:Observed x:Key='derived'/></local:BaseTheme>")
        }, sourceCode: Code.Replace("CONSTRUCTOR", constructor));
    }
    [AvaloniaFact]
    public void FactoriesInitializeEveryCodeBehindLayerAndRetainItsOwnedSubscriptions()
    {
        var root = (ResourceDictionary)Project().Build("Theme.axaml", new Services());
        var first = root["base"]!; var second = root["derived"]!;
        Assert.Equal("factory services", first.GetType().GetField("Payload")!.GetValue(first));
        Assert.Equal("factory services", second.GetType().GetField("Payload")!.GetValue(second));
        var count = first.GetType().GetField("Disposals")!;
        Assert.Equal(0, count.GetValue(null));
        root.GetType().GetMethod("Reinitialize")!.Invoke(root, null);
        Assert.Same(first, root["base"]); Assert.Same(second, root["derived"]);
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
        Assert.Equal(2, count.GetValue(null));
        Assert.Null(XamlConstructionScope.GetServicesFor(root));
    }
    [AvaloniaFact]
    public void DerivedConstructorFailureRetiresItsAlreadyInitializedBaseGraph()
    {
        var project = Project("throw new InvalidOperationException(\"derived failed\");");
        var assembly = ResourceProjectFixture.Load(project.Emit());
        var output = project.Result.Documents.Single(d => d.Input.LogicalPath == "Theme.axaml").Output;
        var failure = Assert.Throws<TargetInvocationException>(() => assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, new object?[] { new Services() }));
        Assert.Equal("derived failed", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        Assert.Equal(1, assembly.GetType("Resources.Observed")!.GetField("Disposals")!.GetValue(null));
        Assert.Null(XamlConstructionScope.GetServices(assembly.GetType("Resources.Theme")!));
    }
}
