using XamlG.Tooling;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class CSharpProjectDocumentStoreTests
{
    [Fact]
    public void Retired_editor_cannot_update_a_recreated_or_replaced_file()
    {
        var store = new CSharpProjectDocumentStore(["Code.cs"]);
        var old = store.Add("Models/State.cs", "class State {}");
        store.Remove(old.Path, old.Version);
        var recreated = store.Add(old.Path, "class Replacement {}");
        Assert.Throws<InvalidOperationException>(() => store.Update(old.Path, old.Version, "stale editor"));
        Assert.Same(recreated, store.Snapshot[old.Path]);
        store.ReplaceAll(new Dictionary<string, string> { [old.Path] = recreated.Text });
        Assert.Throws<InvalidOperationException>(() => store.Update(old.Path, recreated.Version, "another stale editor"));
        Assert.Equal("class Replacement {}", store.Snapshot[old.Path].Text);
    }

    [Fact]
    public void Invalid_replacement_keeps_both_source_identity_and_revision()
    {
        var store = new CSharpProjectDocumentStore(["Code.cs"], maximumCharacters: 32);
        var original = store.Add("Models/State.cs", "class State {}");
        var revision = store.Revision;
        Assert.Throws<ArgumentException>(() => store.ReplaceAll(new Dictionary<string, string>
        { [original.Path] = "changed", ["Code.cs"] = "host file" }));
        Assert.Throws<InvalidOperationException>(() => store.ReplaceAll(new Dictionary<string, string>
        { [original.Path] = "changed", ["Other.cs"] = new('x', 32) }));
        Assert.Same(original, store.Snapshot[original.Path]);
        Assert.Equal(revision, store.Revision);
        Assert.Throws<ArgumentException>(() => store.Add("../Escape.cs", ""));
        Assert.Throws<ArgumentException>(() => store.Add("C:/Escape.cs", ""));
        Assert.Throws<ArgumentException>(() => store.Add("View.axaml", ""));
    }
}
