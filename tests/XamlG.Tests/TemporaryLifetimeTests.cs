using System.Collections;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class TemporaryLifetimeTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace Model {
          public class Panel {
            [Content] public List<Panel> Children { get; } = new();
            public string Text { get; set; }
            public object Value { get; set; }
          }
          public class CaptureExtension {
            public CaptureExtension(string text) { Text = text; }
            public string Text { get; }
            public object ProvideValue(IServiceProvider provider) => new Snapshot { Provider = provider, Text = Text };
          }
          public class Snapshot { public IServiceProvider Provider { get; set; } public string Text { get; set; } }
          public class Template {
            [Content, DeferredContent] public Func<IServiceProvider, object> Content { get; set; }
          }
        }
        """;

    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    [Fact]
    public void RetainedProvidersAndLiveSettersKeepEachSiblingTarget()
    {
        var children = string.Concat(Enumerable.Range(0, 24).Select(index =>
            "<Panel Text='text" + index + "' Value='{Capture argument" + index + "}'/>"));
        using var code = CompiledXaml.Create("<Panel xmlns='clr-namespace:Model'>" + children + "</Panel>", Model);
        var root = code.Build();
        var built = (IList)Property(root, "Children")!;
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        for (var index = 0; index < built.Count; index++)
        {
            var child = built[index]!;
            var snapshot = Property(child, "Value")!;
            Assert.Equal("argument" + index, Property(snapshot, "Text"));
            var provider = (IServiceProvider)Property(snapshot, "Provider")!;
            var target = Assert.IsAssignableFrom<IXamlProvideValueTarget>(provider.GetService(typeof(IXamlProvideValueTarget)));
            Assert.Same(child, target.TargetObject);
            Assert.Equal("Value", Assert.IsAssignableFrom<System.Reflection.PropertyInfo>(target.TargetProperty).Name);
            var parents = Assert.IsAssignableFrom<IXamlParentStackProvider>(provider.GetService(typeof(IXamlParentStackProvider)));
            Assert.Contains(child, parents.Parents);
            Assert.Contains(root, parents.Parents);
            Assert.True(session!.Apply(index, new[] { new XamlPropertyUpdate(session.FindNode(child)!.Key, "Text", "edited" + index) }).Applied);
        }
        for (var index = 0; index < built.Count; index++) Assert.Equal("edited" + index, Property(built[index]!, "Text"));
    }

    [Fact]
    public void CapturedDeferredScopesKeepTheirOriginalParents()
    {
        const string child = "<Panel><Panel.Value><Template><Panel Value='{Capture later}'/></Template></Panel.Value></Panel>";
        using var code = CompiledXaml.Create("<Panel xmlns='clr-namespace:Model'>" + child + child + "</Panel>", Model);
        var root = code.Build();
        var siblings = (IList)Property(root, "Children")!;
        foreach (var sibling in siblings)
        {
            var template = Property(sibling!, "Value")!;
            var factory = (Delegate)Property(template, "Content")!;
            for (var invocation = 0; invocation < 2; invocation++)
            {
                var content = factory.DynamicInvoke(new object?[] { null })!;
                var snapshot = Property(content, "Value")!;
                var provider = (IServiceProvider)Property(snapshot, "Provider")!;
                var parents = Assert.IsAssignableFrom<IXamlParentStackProvider>(provider.GetService(typeof(IXamlParentStackProvider)));
                Assert.Contains(sibling, parents.Parents);
                Assert.DoesNotContain(siblings.Cast<object>().Single(other => !ReferenceEquals(other, sibling)), parents.Parents);
            }
        }
    }
}
