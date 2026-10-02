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
}
