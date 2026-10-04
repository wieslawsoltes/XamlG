using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class ProjectDocumentStoreTests
{
    [Fact]
    public void EditsRetainUnchangedNodesAndRejectSupersededRevisions()
    {
        var store = new XamlProjectDocumentStore();
        var original = store.Add("Values.axaml", "<Root><A Text='old'/><B/></Root>");
        var updated = store.Update(original.Path, original.Version, original.Text.Replace("old", "new"));
        Assert.Same(original.Root!.Children[1], updated.Root!.Children[1]);
        Assert.Throws<InvalidOperationException>(() => store.Update(original.Path, original.Version, "<Stale/>"));
        Assert.Same(updated, store.Snapshot[original.Path]);
    }
    [Fact]
    public void ReplacingAProjectIsAtomicAndDoesNotReuseOldRevisions()
    {
        var store = new XamlProjectDocumentStore(); var original = store.Add("A.xaml", "<A/>");
        Assert.Throws<ArgumentException>(() => store.ReplaceAll(new Dictionary<string, string> { ["B.xaml"] = "<B/>", ["../Bad.xaml"] = "<Bad/>" }));
        Assert.Same(original, Assert.Single(store.Snapshot).Value);
        store.ReplaceAll(new Dictionary<string, string> { ["A.xaml"] = "<Replacement/>" });
        Assert.True(store.Snapshot["A.xaml"].Version > original.Version);
        Assert.Throws<InvalidOperationException>(() => store.Remove("A.xaml", original.Version));
    }
    [Theory]
    [InlineData("/absolute.xaml")]
    [InlineData("../escape.xaml")]
    [InlineData("A//B.xaml")]
    [InlineData("C:/Root.xaml")]
    [InlineData("code.cs")]
    public void InvalidProjectPathsAreRejected(string path) => Assert.Throws<ArgumentException>(() => new XamlProjectDocumentStore().Add(path, "<A/>"));
    [Fact]
    public void ReservedPathsAndSizeLimitsCannotBeBypassedThroughReplacement()
    {
        var store = new XamlProjectDocumentStore(new[] { "View.axaml" }, maximumDocuments: 1, maximumCharacters: 8);
        Assert.Throws<ArgumentException>(() => store.Add("View.axaml", "<A/>"));
        store.Add("A.xaml", "<A/>");
        Assert.Throws<InvalidOperationException>(() => store.Add("B.xaml", "<B/>"));
        Assert.Throws<InvalidOperationException>(() => store.ReplaceAll(new Dictionary<string, string> { ["B.xaml"] = "<TooLarge/>" }));
        Assert.Contains("A.xaml", store.Snapshot.Keys);
    }
}
