using XamlG.Runtime;
using Xunit;
namespace XamlG.Tests;
public sealed class RuntimeTests
{
    [Fact]
    public void FramesPreserveTargetsAndParentOrder()
    {
        var root = new object(); var child = new object(); var context = new XamlRuntimeContext(root: root);
        var frame = context.Push(root, "root").Push(child, "child").ForTarget(child, "Text");
        Assert.Same(root, frame.RootObject); Assert.Equal(new[] { child, root }, frame.Parents); Assert.Equal("Text", frame.TargetProperty);
    }
    [Fact]
    public void DirectParentStacksAreSharedByTargetFramesAndIsolateBranches()
    {
        var root = new object(); var first = new object(); var second = new object();
        var context = new XamlRuntimeContext();
        var parent = context.Push(root, "root");
        var firstFrame = parent.Push(first, "first");
        var secondFrame = parent.Push(second, "second");
        Assert.Empty(context.DirectParentsStack);
        Assert.Equal(new[] { root }, parent.DirectParentsStack);
        Assert.Equal(new[] { root, first }, firstFrame.DirectParentsStack);
        Assert.Equal(new[] { root, second }, secondFrame.DirectParentsStack);
        Assert.Same(firstFrame.DirectParentsStack, firstFrame.ForTarget(first, "Text").DirectParentsStack);
        Assert.Same(firstFrame.DirectParentsStack, firstFrame.WithNamespaces(new Dictionary<Type, object>()).DirectParentsStack);
        Assert.Equal(firstFrame.Parents.Reverse(), firstFrame.DirectParentsStack);
    }
    [Fact]
    public void UriMutationIsSharedWithinConstructionButIsolatedAcrossDeferredScopes()
    {
        var original = new Uri("https://original.example/");
        var updated = new Uri("https://updated.example/");
        var deferredUri = new Uri("https://deferred.example/");
        var context = new XamlRuntimeContext(baseUri: original);
        var first = context.Push(new object(), "first").ForTarget(new object(), "Text");
        var second = context.Push(new object(), "second");
        first.BaseUri = updated;
        Assert.Same(updated, second.BaseUri);
        var deferred = second.CreateDeferredScope();
        Assert.Same(updated, deferred.BaseUri);
        deferred.ForTarget(new object(), "Text").BaseUri = deferredUri;
        Assert.Same(deferredUri, deferred.BaseUri);
        Assert.Same(updated, context.BaseUri);
        first.BaseUri = null;
        Assert.Null(second.BaseUri);
        Assert.Same(deferredUri, deferred.BaseUri);
    }
    [Fact]
    public void ForwardNameFixupsRunAtCompletion()
    {
        var context = new XamlRuntimeContext(); var expected = new object(); object? actual = null;
        context.Defer(() => actual = context.ResolveName<object>("later")); context.RegisterName("later", expected); context.Complete(new object());
        Assert.Same(expected, actual);
    }
    [Fact]
    public void PropertyBatchIsRevisionCheckedAndRollbackSafe()
    {
        using var session = new XamlRuntimeSession(); var a = 1; var b = 2;
        session.RegisterProperty("root", "A", () => a, value => a = value);
        session.RegisterProperty("root", "B", () => b, value => { if (value == 99) throw new InvalidOperationException("Rejected"); b = value; });
        var rejected = session.Apply(0, new[] { new XamlPropertyUpdate("root", "A", 5), new XamlPropertyUpdate("root", "B", 99) });
        Assert.False(rejected.Applied); Assert.Equal(1, a); Assert.Equal(2, b); Assert.Equal(0, session.Revision);
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate("root", "A", 7) }).Applied); Assert.Equal(7, a);
        Assert.False(session.Apply(0, Array.Empty<XamlPropertyUpdate>()).Applied);
    }
    [Fact]
    public void BatchRejectsInvalidTypesBeforeCallingAnySetter()
    {
        using var session = new XamlRuntimeSession(); var value = 1;
        session.RegisterProperty("root", "Value", () => value, replacement => value = replacement);
        Assert.False(session.Apply(0, new[] { new XamlPropertyUpdate("root", "Value", "bad") }).Applied); Assert.Equal(1, value);
    }
    private sealed class AccessorTarget { public int Value { get; set; } }
    [Fact]
    public void SharedAccessorsKeepTargetsIndependentAndRollBackFailedBatches()
    {
        var types = new[] { typeof(int) };
        var accessor = new XamlPropertyTable(types, static (target, _) => ((AccessorTarget)target).Value,
            static (target, _, value) => { ((AccessorTarget)target).Value = (int)value!; if ((int)value == 99) throw new InvalidOperationException("Rejected"); });
        types[0] = typeof(string);
        var first = new AccessorTarget { Value = 1 }; var second = new AccessorTarget { Value = 2 };
        using var session = new XamlRuntimeSession();
        accessor.Register(session, "first", "Value", first, 0);
        accessor.Register(session, "second", "Value", second, 0);
        Assert.False(session.Apply(0, new[] { new XamlPropertyUpdate("first", "Value", 8), new XamlPropertyUpdate("second", "Value", 99) }).Applied);
        Assert.Equal(1, first.Value); Assert.Equal(2, second.Value); Assert.Equal(0, session.Revision);
        Assert.False(session.Apply(0, new[] { new XamlPropertyUpdate("first", "Value", "bad") }).Applied);
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate("first", "Value", 8) }).Applied);
        Assert.Equal(8, first.Value); Assert.Equal(2, second.Value);
    }
}
