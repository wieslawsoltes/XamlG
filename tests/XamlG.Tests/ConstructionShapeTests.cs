using System.Collections;
using System.Reflection;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class ConstructionShapeTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using XamlG.Runtime;
        namespace Shapes;
        public class View
        {
            public static List<string> Log { get; } = new();
            public object First { get; set; }
            public object Second { get; set; }
        }
        public class TrackExtension : ISupportInitialize
        {
            private object _key;
            private int _number;
            public TrackExtension()
            {
                View.Log.Add("new");
                var owner = new XamlRuntimeSession();
                owner.TrackCleanup(() => View.Log.Add("dispose:" + _key));
                owner.Attach(this);
            }
            public object Key
            {
                get => _key;
                set { View.Log.Add("key:" + value); _key = value; }
            }
            public int Number
            {
                get => _number;
                set { View.Log.Add("number:" + value); _number = value; }
            }
            public object Target { get; private set; }
            public void BeginInit() => View.Log.Add("begin");
            public void EndInit()
            {
                View.Log.Add("end");
                if (Equals(Key, "fail")) throw new InvalidOperationException("initialization failure");
            }
            public object ProvideValue(IServiceProvider provider)
            {
                View.Log.Add("provide:" + Key);
                Target = ((IXamlProvideValueTarget)provider.GetService(typeof(IXamlProvideValueTarget))).TargetObject;
                return this;
            }
        }
        """;

    [Fact]
    public void RepeatedConstructionPreservesInitializationServicesEditingAndOwnership()
    {
        const string xaml = "<View xmlns='clr-namespace:Shapes' First='{Track Key=alpha, Number=1}' Second='{Track Key=beta, Number=2}'/>";
        using var code = CompiledXaml.Create(xaml, Model);
        var root = code.Build();
        var first = Property(root, "First");
        var second = Property(root, "Second");
        Assert.NotSame(first, second);
        Assert.Same(root, Property(first, "Target"));
        Assert.Same(root, Property(second, "Target"));
        Assert.Equal("new,begin,key:alpha,number:1,end,provide:alpha,new,begin,key:beta,number:2,end,provide:beta", Events(code));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        var firstNode = session!.FindNode(first)!;
        var secondNode = session.FindNode(second)!;
        Assert.NotEqual(firstNode.Key, secondNode.Key);
        Assert.NotNull(firstNode.Source);
        Assert.NotNull(secondNode.Source);
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate(firstNode.Key, "Key", "changed") }).Applied);
        Assert.Equal("changed", Property(first, "Key"));
        Assert.Equal("beta", Property(second, "Key"));
        session.Dispose();
        Assert.EndsWith("dispose:beta,dispose:changed", Events(code), StringComparison.Ordinal);
        foreach (var literal in new[] { "alpha", "beta" })
            Assert.Contains(code.Emission.SourceMappings, mapping =>
                code.Emission.Source.Substring(mapping.GeneratedSpan.Start, mapping.GeneratedSpan.Length).Contains(literal, StringComparison.Ordinal) &&
                xaml.Substring(mapping.SourceSpan.Start, mapping.SourceSpan.Length).Contains(literal, StringComparison.Ordinal));
    }

    [Fact]
    public void FailedInitializationRetainsCleanupAndTheOriginalException()
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Shapes' First='{Track Key=good}' Second='{Track Key=fail}'/>", Model);
        var failure = Assert.Throws<TargetInvocationException>(() => code.Build());
        Assert.Equal("initialization failure", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        Assert.Equal("new,begin,key:good,end,provide:good,new,begin,key:fail,end,dispose:fail,dispose:good", Events(code));
    }

    private static object Property(object target, string property) => target.GetType().GetProperty(property)!.GetValue(target)!;
    private static string Events(CompiledXaml code) => string.Join(",",
        ((IEnumerable)code.Assembly.GetType("Shapes.View")!.GetProperty("Log")!.GetValue(null)!).Cast<string>());
}
