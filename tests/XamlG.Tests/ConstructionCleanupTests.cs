using System.Reflection;
using Xunit;

namespace XamlG.Tests;

public sealed class ConstructionCleanupTests
{
    [Fact]
    public void NewlyConstructedRootsKeepConstructorSessionsUntilTheirOwnerIsDisposed()
    {
        const string model = """
            using XamlG.Runtime;
            namespace Lifecycle {
                public class InitializedView {
                    public XamlRuntimeSession ConstructorSession { get; }
                    public int Released;
                    public InitializedView() {
                        ConstructorSession = new XamlRuntimeSession();
                        ConstructorSession.TrackCleanup(() => Released++);
                        ConstructorSession.Attach(this);
                    }
                }
            }
            """;
        using var code = CompiledXaml.Create("<InitializedView xmlns='clr-namespace:Lifecycle'/>", model);
        var root = code.Build();
        var constructor = (XamlG.Runtime.XamlRuntimeSession)root.GetType().GetProperty("ConstructorSession")!.GetValue(root)!;
        Assert.False(constructor.IsDisposed);
        Assert.True(XamlG.Runtime.XamlRuntimeSession.TryGet(root, out var owner));
        Assert.NotSame(constructor, owner);
        owner!.Dispose();
        owner.Dispose();
        Assert.True(constructor.IsDisposed);
        Assert.Equal(1, root.GetType().GetField("Released")!.GetValue(root));
    }

    private const string Model = """
        using System;
        using XamlG.Runtime;
        namespace Lifecycle {
            public class View {
                public string First { get; set; }
                public string Failure { get => ""; set => throw new InvalidOperationException("setter failure"); }
            }
            public class TrackExtension {
                public static int Released;
                public static bool FailCleanup;
                public string ProvideValue(IServiceProvider provider) {
                    var context = (XamlRuntimeContext)provider.GetService(typeof(XamlRuntimeContext));
                    context.Session.TrackCleanup(() => {
                        Released++;
                        if (FailCleanup) throw new InvalidOperationException("cleanup failure");
                    });
                    return "tracked";
                }
            }
        }
        """;

    [Fact]
    public void FailedConstructionDisposesRegisteredLifetimesAndPreservesTheOriginalError()
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Lifecycle' First='{Track}' Failure='fail'/>", Model);
        var error = Assert.Throws<TargetInvocationException>(() => code.Build());
        Assert.Equal("setter failure", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
        Assert.Equal(1, code.Assembly.GetType("Lifecycle.TrackExtension")!.GetField("Released")!.GetValue(null));
    }

    [Fact]
    public void CleanupFailureDoesNotHideConstructionFailure()
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Lifecycle' First='{Track}' Failure='fail'/>", Model);
        code.Assembly.GetType("Lifecycle.TrackExtension")!.GetField("FailCleanup")!.SetValue(null, true);
        var error = Assert.Throws<TargetInvocationException>(() => code.Build());
        var failures = Assert.IsType<AggregateException>(error.InnerException).Flatten().InnerExceptions;
        Assert.Contains(failures, failure => failure.Message == "setter failure");
        Assert.Contains(failures, failure => failure.Message == "cleanup failure");
    }
}
