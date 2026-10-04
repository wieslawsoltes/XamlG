using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class DocumentSetTests
{
    [Fact]
    public void ADependencyEditInvalidatesTheSetWithoutChangingItsCallerVersion()
    {
        var store = new LspDocumentStore();
        var caller = store.Open("file:///Main.axaml", 1, "<Root/>");
        store.Open("file:///Values.axaml", 1, "<Values/>");
        var before = store.Capture();
        store.Change("file:///Values.axaml", 2, new[] { new LspTextChange(null, "<Changed/>") });
        Assert.True(store.IsCurrent(caller)); Assert.False(store.IsCurrent(before));
        var changed = store.Capture(); Assert.True(store.IsCurrent(changed));
        store.Close("file:///Values.axaml");
        Assert.False(store.IsCurrent(changed)); Assert.Single(store.Capture().Documents);
    }
    [Fact]
    public void RejectedChangesDoNotInvalidateTheCurrentSet()
    {
        var store = new LspDocumentStore(); store.Open("file:///Main.axaml", 3, "<Root/>");
        var before = store.Capture();
        Assert.Throws<LspRequestException>(() => store.Change("file:///Main.axaml", 2, Array.Empty<LspTextChange>()));
        Assert.True(store.IsCurrent(before));
    }
    [Fact]
    public void UriAliasesCannotIntroduceTwoBuffersForTheSameSourcePath()
    {
        var store = new LspDocumentStore(); store.Open("file:///View.xaml", 1, "<Root/>");
        Assert.Throws<LspRequestException>(() => store.Open("file:///View.%78aml", 1, "<Other/>"));
        Assert.Single(store.Snapshots);
    }
}
