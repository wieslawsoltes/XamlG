using XamlG.Runtime;
using XamlG.Runtime.Reload;
using Xunit;

namespace XamlG.Tests;

public sealed class ReloadTests
{
    private sealed class Model { public string Text { get; set; } = "initial"; }
    private sealed class Adapter : IXamlStateTransferAdapter
    {
        public bool Fail { get; set; }
        public XamlStateTransferOperation? Prepare(XamlStateTransferContext context)
        {
            if (context.Previous.Instance is not Model before || context.Candidate.Instance is not Model after || !context.IsDeclarationUnchanged("Text")) return null;
            var state = before.Text;
            return new(() => { after.Text = state; if (Fail) throw new InvalidOperationException("Restore failed."); });
        }
    }
    private static Model Create(string declaration = "same", string? identity = "root")
    {
        var model = new Model();
        var session = new XamlRuntimeSession();
        session.Register("n0", model, null);
        session.RegisterSource("n0", new("View.xaml", 0, 10, identity, declaration, new Dictionary<string, string> { ["Text"] = declaration }));
        session.Attach(model);
        return model;
    }
    [Fact]
    public void SuccessfulReloadTransfersOnlyUnchangedDeclaredState()
    {
        var old = Create(); old.Text = "user input";
        var host = new XamlReloadSession(old, new[] { new Adapter() }); object displayed = old;
        var next = Create();
        var result = host.Reload(0, () => next, value => displayed = value);
        Assert.True(result.Applied); Assert.Same(next, displayed); Assert.Equal("user input", next.Text);
        Assert.False(XamlRuntimeSession.TryGet(old, out _));
        var changed = Create("changed"); changed.Text = "new source wins";
        Assert.True(host.Reload(1, () => changed, value => displayed = value).Applied);
        Assert.Equal("new source wins", changed.Text);
    }
    [Fact]
    public void FailedCandidateDoesNotMutateOrRetireTheDisplayedGraph()
    {
        var old = Create(); old.Text = "keep"; var next = Create(); object displayed = old;
        var host = new XamlReloadSession(old, new[] { new Adapter { Fail = true } });
        var result = host.Reload(0, () => next, value => displayed = value);
        Assert.False(result.Applied); Assert.Same(old, displayed); Assert.Equal("keep", old.Text);
        Assert.True(XamlRuntimeSession.TryGet(old, out _)); Assert.False(XamlRuntimeSession.TryGet(next, out _));
        Assert.Equal(0, host.Revision);
    }
    [Fact]
    public void FailedPublicationRestoresThePreviousRoot()
    {
        var old = Create(); var next = Create(); object displayed = old;
        var host = new XamlReloadSession(old);
        var result = host.Reload(0, () => next, value => { displayed = value; if (ReferenceEquals(value, next)) throw new InvalidOperationException("Host refused replacement."); });
        Assert.False(result.Applied); Assert.Same(old, displayed); Assert.Same(old, host.Root);
    }
    [Fact]
    public void StaleRequestsNeverInvokeApplicationFactories()
    {
        var host = new XamlReloadSession(Create());
        Assert.False(host.Reload(1, () => throw new Exception("Must not run."), _ => throw new Exception()).Applied);
    }
    [Fact]
    public void PostCommitCleanupFailureIsReportedWithoutUndoingAValidReplacement()
    {
        var old = Create(); XamlRuntimeSession.TryGet(old, out var session);
        session!.TrackCleanup(() => throw new InvalidOperationException("Cleanup failed."));
        var next = Create(); var host = new XamlReloadSession(old);
        var result = host.Reload(0, () => next, _ => { });
        Assert.True(result.Applied); Assert.Single(result.Warnings); Assert.Same(next, host.Root);
    }
    [Fact]
    public void AnonymousPositionKeysDoNotAliasAfterStructuralEdits()
    {
        var old = new XamlRuntimeSession(); var next = new XamlRuntimeSession();
        var root1 = new object(); var root2 = new object();
        old.Register("n0", root1, null); next.Register("n0", root2, null);
        old.Register("n1", new Model(), "n0"); next.Register("n1", new Model(), "n0");
        old.RegisterSource("n1", new("View.xaml", 1, 3, null, "old"));
        next.RegisterSource("n1", new("View.xaml", 1, 3, null, "inserted"));
        Assert.Empty(XamlNodeMatcher.Match(old, next, root1, root2));
    }
}
