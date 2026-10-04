using System.Reflection;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class CodeBehindFactoryTests
{
    private const string Xaml = "<Base xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Model.View' Value='{Payload}'/>";
    private const string Base = """
        using System;
        using XamlG.Runtime;
        namespace Model {
          public class Base { public string Value {get;set;} }
          public class PayloadExtension {
            public object ProvideValue(IServiceProvider services) => services.GetService(typeof(string));
          }
        }
        """;
    private sealed class Services(string text) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(string) ? text : null;
    }
    [Theory]
    [InlineData("public View() { InitializeComponent(); Calls++; }")]
    [InlineData("public View() { Calls++; }")]
    [InlineData("public View(IServiceProvider services) { InitializeComponent(); Calls++; }")]
    public void FactoriesPreserveConstructorsAndSupplyInitializationServices(string constructor)
    {
        using var compiled = CompiledXaml.Create(Xaml, Base + "namespace Model { public partial class View : Base { public static int Calls; " + constructor + " } }");
        Assert.NotNull(compiled.Emission.BuildMethodName);
        var root = compiled.Build(new Services("inherited"));
        Assert.Equal("inherited", root.GetType().GetProperty("Value")!.GetValue(root));
        Assert.Equal(1, root.GetType().GetField("Calls")!.GetValue(null));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session)); session!.Dispose();
        Assert.Null(XamlConstructionScope.GetServices(root.GetType()));
    }
    [Fact]
    public void AutomaticInitializationSetsTheSameIdempotenceGuardAsConstructorInitialization()
    {
        const string source = "namespace Model { public partial class View : Base { public void Reinitialize() => InitializeComponent(); } }";
        using var compiled = CompiledXaml.Create(Xaml, Base + source);
        var root = compiled.Build(new Services("once"));
        Assert.True(XamlRuntimeSession.TryGet(root, out var original));
        root.GetType().GetMethod("Reinitialize")!.Invoke(root, null);
        Assert.True(XamlRuntimeSession.TryGet(root, out var next));
        Assert.Same(original, next);
        Assert.Equal("once", root.GetType().GetProperty("Value")!.GetValue(root));
        next!.Dispose();
    }
    [Fact]
    public void ConstructorFailureAfterInitializationRetiresItsSession()
    {
        using var compiled = CompiledXaml.Create(Xaml, Base + """
            namespace Model { public partial class View : Base {
              public static View Last;
              public View() { InitializeComponent(); Last = this; throw new InvalidOperationException("constructor failed"); }
            } }
            """);
        var error = Assert.Throws<TargetInvocationException>(() => compiled.Build(new Services("value")));
        Assert.Equal("constructor failed", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
        var type = compiled.Assembly.GetType("Model.View")!;
        Assert.False(XamlRuntimeSession.TryGet(type.GetField("Last")!.GetValue(null)!, out _));
        Assert.Null(XamlConstructionScope.GetServices(type));
    }
    [Fact]
    public void ConstructorAndCleanupFailuresAreBothPreserved()
    {
        const string source = """
            using System; using XamlG.Runtime;
            namespace Model {
              public class Base { public string Value {get;set;} }
              public class PayloadExtension {
                public object ProvideValue(IServiceProvider services) {
                  ((XamlRuntimeContext)services.GetService(typeof(XamlRuntimeContext))).Session.TrackCleanup(() => throw new InvalidOperationException("cleanup failed"));
                  return "value";
                }
              }
              public partial class View : Base {
                public View() { InitializeComponent(); throw new InvalidOperationException("constructor failed"); }
              }
            }
            """;
        using var compiled = CompiledXaml.Create(Xaml, source);
        var failure = Assert.Throws<TargetInvocationException>(() => compiled.Build());
        var aggregate = Assert.IsType<AggregateException>(failure.InnerException).Flatten();
        Assert.Contains(aggregate.InnerExceptions, e => e.Message == "constructor failed");
        Assert.Contains(aggregate.InnerExceptions, e => e.Message == "cleanup failed");
    }
    [Fact]
    public void HandwrittenInitializersRemainExplicitPopulateOnly()
    {
        using var compiled = CompiledXaml.Create(Xaml, Base + "namespace Model { public partial class View : Base { private void InitializeComponent() { } } }");
        Assert.Null(compiled.Emission.BuildMethodName);
        Assert.DoesNotContain("XamlConstructionScope.Begin", compiled.Emission.Source);
    }
    [Fact]
    public void RequiredMembersWithoutAnAnnotatedConstructorKeepPopulateOnlySupport()
    {
        using var compiled = CompiledXaml.Create(Xaml, Base + "namespace Model { public partial class View : Base { public required string Required {get;set;} } }");
        Assert.Null(compiled.Emission.BuildMethodName);
    }
    [Fact]
    public void NestedCodeBehindFactoriesExposeSeparateCSharpAndMetadataNames()
    {
        using var compiled = CompiledXaml.Create(Xaml.Replace("Model.View", "Model.Outer+View"), Base +
            "namespace Model { public partial class Outer { public partial class View : Base { public View() { InitializeComponent(); } } } }");
        Assert.Equal("Model.Outer.View", compiled.Emission.FactoryTypeName);
        Assert.Equal("Model.Outer+View", compiled.Emission.FactoryMetadataName);
        var root = compiled.Build(new Services("nested"));
        Assert.Equal("nested", root.GetType().GetProperty("Value")!.GetValue(root));
    }
    [Theory]
    [InlineData(" x:FactoryMethod='Create'", "")]
    [InlineData("", "<x:Arguments><x:String>argument</x:String></x:Arguments>")]
    public void ExplicitConstructionIsNotSilentlyReplacedByAnAutomaticConstructor(string directive, string arguments)
    {
        var source = Base + "namespace Model { public partial class View : Base { public View() { } public View(string value) { } public static View Create() => new View(); } }";
        var xaml = Xaml.Replace("/>", directive + ">" + arguments + "</Base>");
        using var compiled = CompiledXaml.Create(xaml, source);
        Assert.Null(compiled.Emission.BuildMethodName);
    }
    [Fact]
    public void ScopeOwnershipDoesNotFlowToOtherThreadsAndRestoresParents()
    {
        var first = new Services("first"); var second = new Services("second");
        using (XamlConstructionScope.Begin(typeof(string), first))
        {
            Assert.Same(first, XamlConstructionScope.GetServices(typeof(string)));
            Assert.Null(XamlConstructionScope.GetServices(typeof(int)));
            using (XamlConstructionScope.Begin(typeof(int), second))
            {
                Assert.Same(second, XamlConstructionScope.GetServices(typeof(int)));
                Assert.Null(XamlConstructionScope.GetServices(typeof(string)));
            }
            Assert.Same(first, XamlConstructionScope.GetServices(typeof(string)));
            object? foreign = first;
            var thread = new Thread(() => foreign = XamlConstructionScope.GetServices(typeof(string)));
            thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(foreign);
        }
        Assert.Null(XamlConstructionScope.GetServices(typeof(string)));
    }
}
