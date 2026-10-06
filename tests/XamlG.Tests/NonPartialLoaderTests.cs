using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class NonPartialLoaderTests
{
    [Theory]
    [InlineData("Loader.Load(this);")]
    [InlineData("Loader.Load((object)this); Loader.Load((object)this);")]
    public void OriginalNonPartialClassUsesAnExternalFactory(string body)
    {
        var source = "namespace Model { public class View : Root { public View() { " + body + " } } }";
        var fixture = new LoaderAdapterFixture(source, ("View.axaml", "<Root " + LoaderAdapterFixture.Namespace +
            " x:Class='Model.View' Text='external'/>") );
        var root = fixture.Build("View.axaml");
        Assert.Equal("external", root.GetType().GetProperty("Text")!.GetValue(root));
        Assert.Equal(1, root.GetType().GetField("Writes")!.GetValue(root));
        var output = fixture.Project.Documents.Single().Output;
        Assert.NotEqual("Model.View", output.FactoryTypeName);
        Assert.Contains(source, fixture.Compilation.SyntaxTrees.Single().ToString());
        Assert.DoesNotContain("partial class View", output.Source);
    }

    [Fact]
    public void InitializationFailureResetsStateWithoutSwallowingTheError()
    {
        var state = new XamlComponentInitializationState();
        var value = new object();
        var attempts = 0;
        Assert.Throws<InvalidOperationException>(() => state.Initialize(value, null, (_, _) =>
        { attempts++; throw new InvalidOperationException("first attempt"); }));
        state.Initialize(value, null, (_, _) => attempts++);
        state.Initialize(value, null, (_, _) => attempts++);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ReentrantAttemptIsRejectedAndOuterFailureRemainsRetryable()
    {
        var state = new XamlComponentInitializationState();
        var value = new object();
        Assert.Throws<InvalidOperationException>(() => state.Initialize(value, null, (_, _) =>
            state.Initialize(value, null, (_, _) => { })));
        var count = 0;
        state.Initialize(value, null, (_, _) => count++);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ConcurrentCallersInitializeOneInstanceOnce()
    {
        var state = new XamlComponentInitializationState();
        var value = new object();
        var count = 0;
        Parallel.For(0, 32, _ => state.Initialize(value, null, (_, _) => Interlocked.Increment(ref count)));
        Assert.Equal(1, count);
    }
}
