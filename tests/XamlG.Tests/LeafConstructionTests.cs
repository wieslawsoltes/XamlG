using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class LeafConstructionTests
{
    [Fact]
    public void RepeatedLeafExtensionsRetainInitializationServicesAndDistinctSourceNodes()
    {
        const string model = """
            using System;
            using System.ComponentModel;
            using XamlG.Runtime;
            namespace Leaf;
            public class View
            {
                public static System.Collections.Generic.List<string> Log { get; } = new();
                public object First { get; set; }
                public object Second { get; set; }
                public string Events => string.Join(",", Log);
            }
            public class TrackedExtension : ISupportInitialize
            {
                private readonly string _name;
                public TrackedExtension(string name) { _name = name; View.Log.Add("new:" + name); }
                public void BeginInit() => View.Log.Add("begin:" + _name);
                public void EndInit() => View.Log.Add("end:" + _name);
                public object ProvideValue(IServiceProvider services)
                {
                    var frame = (XamlRuntimeContext)services.GetService(typeof(XamlRuntimeContext));
                    if (frame.TargetObject is not View || frame.Session.FindNode(this) == null)
                        throw new Exception("Lost target services or extension tracking");
                    View.Log.Add("provide:" + _name);
                    return this;
                }
            }
            """;
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Leaf' First='{Tracked one}' Second='{Tracked two}'/>", model);
        var root = code.Build();
        Assert.Equal("new:one,begin:one,end:one,provide:one,new:two,begin:two,end:two,provide:two",
            root.GetType().GetProperty("Events")!.GetValue(root));
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        var first = session!.FindNode(root.GetType().GetProperty("First")!.GetValue(root)!);
        var second = session.FindNode(root.GetType().GetProperty("Second")!.GetValue(root)!);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(session.FindNode(root)!.Key, first.ParentKey);
        Assert.Equal(first.ParentKey, second.ParentKey);
        Assert.NotEqual(first.Source!.Start, second.Source!.Start);
    }
}
