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
    public void FramesRetainIndependentSourceMappingsAndLogicalParents()
    {
        var root = new object(); var child = new object(); var sibling = new object();
        var rootSource = new XamlSourceInfo("View.xaml", 0, 30, null, "root");
        var childSource = new XamlSourceInfo("View.xaml", 6, 10, "named", "child");
        var context = new XamlRuntimeContext();
        var parent = context.PushRoot(root, "root", rootSource);
        var frame = parent.ForTarget(root, "Content").Push(child, "child", childSource);
        parent.Push(sibling, "sibling");
        Assert.Same(root, frame.RootObject);
        Assert.Same(rootSource, context.Session.FindNode(root)!.Source);
        Assert.Same(childSource, context.Session.FindNode(child)!.Source);
        Assert.Equal("root", context.Session.FindNode(child)!.ParentKey);
        Assert.Null(context.Session.FindNode(sibling)!.Source);
        Assert.Equal("root", context.Session.FindNode(sibling)!.ParentKey);
        Assert.Throws<InvalidOperationException>(() => parent.Push(new object(), "child", childSource));
        Assert.Same(childSource, context.Session.FindNode(child)!.Source);
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
    [Fact]
    public void NamedAccessorsUseConstructionNodesAcrossTargetAndNamespaceFrames()
    {
        var types = new[] { typeof(int) }; var names = new[] { "Value" };
        var accessor = new XamlPropertyTable(types, names, static (target, _) => ((AccessorTarget)target).Value,
            static (target, _, value) => ((AccessorTarget)target).Value = (int)value!);
        types[0] = typeof(string); names[0] = "Changed";
        var first = new AccessorTarget { Value = 1 }; var second = new AccessorTarget { Value = 2 };
        var unrelated = new AccessorTarget { Value = 3 };
        var context = new XamlRuntimeContext();
        var parent = context.PushRoot(first, "first");
        var child = parent.Push(second, "second");
        accessor.Register(parent.ForTarget(unrelated, "Other"), 0);
        accessor.Register(child.WithNamespaces(new Dictionary<Type, object>()).ForTarget(first, "Other"), 0);
        using var session = context.Session;
        Assert.False(session.Apply(0, new[] { new XamlPropertyUpdate("first", "Value", "bad") }).Applied);
        Assert.True(session.Apply(0, new[] {
            new XamlPropertyUpdate("first", "Value", 4), new XamlPropertyUpdate("second", "Value", 5)
        }).Applied);
        Assert.Equal(4, first.Value); Assert.Equal(5, second.Value); Assert.Equal(3, unrelated.Value);

        var deferred = child.CreateDeferredScope();
        using var deferredSession = deferred.Session;
        Assert.Throws<InvalidOperationException>(() => accessor.Register(deferred.ForTarget(first, "Other"), 0));
        accessor.Register(deferred.Push(unrelated, "first"), 0);
        Assert.True(deferredSession.Apply(0, new[] { new XamlPropertyUpdate("first", "Value", 6) }).Applied);
        Assert.Equal(4, first.Value); Assert.Equal(6, unrelated.Value); Assert.Equal(1, session.Revision);
    }
    [Fact]
    public void NamedAccessorsRequireMatchingMetadataAndACurrentNode()
    {
        static object? Get(object target, int index) => null;
        static void Set(object target, int index, object? value) { }
        var types = new[] { typeof(int) };
        Assert.Throws<ArgumentNullException>(() => new XamlPropertyTable(types, null!, Get, Set));
        Assert.Throws<ArgumentException>(() => new XamlPropertyTable(types, Array.Empty<string>(), Get, Set));
        Assert.Throws<ArgumentException>(() => new XamlPropertyTable(types, new string[] { null! }, Get, Set));
        var accessor = new XamlPropertyTable(types, new[] { "Value" }, Get, Set);
        Assert.Throws<ArgumentNullException>(() => accessor.Register(null!, 0));
        var context = new XamlRuntimeContext();
        using var session = context.Session;
        Assert.Throws<InvalidOperationException>(() => accessor.Register(context, 0));
        Assert.Throws<InvalidOperationException>(() => new XamlPropertyTable(types, Get, Set).Register(context.Push(new object(), "root"), 0));
    }
}
