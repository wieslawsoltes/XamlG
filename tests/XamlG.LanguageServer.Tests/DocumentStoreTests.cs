using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class DocumentStoreTests
{
    [Fact]
    public void ChangesUseSequentialUtf16RangesAndRejectStaleVersions()
    {
        var store = new LspDocumentStore();
        const string uri = "file:///View.xaml";
        store.Open(uri, 1, "<Root Text='😀'/>");
        var result = store.Change(uri, 2, new[]
        {
            new LspTextChange(new(new(0, 12), new(0, 14)), "a"),
            new LspTextChange(new(new(0, 12), new(0, 13)), "updated")
        });
        Assert.Equal("<Root Text='updated'/>", result.Syntax.Text);
        Assert.Throws<LspRequestException>(() => store.Change(uri, 2, Array.Empty<LspTextChange>()));
    }

    [Fact]
    public void InvalidBatchesAreAtomic()
    {
        var store = new LspDocumentStore(); const string uri = "file:///View.xaml";
        var original = store.Open(uri, 1, "<Root/>\r\n");
        Assert.Throws<LspRequestException>(() => store.Change(uri, 2, new[] { new LspTextChange(new(new(0, 8), new(0, 9)), "x") }));
        Assert.Same(original, store.Get(uri));
    }

    [Fact]
    public void DocumentLimitsAndSchemesAreEnforced()
    {
        var store = new LspDocumentStore(maximumDocuments: 1, maximumCharacters: 8);
        Assert.Throws<LspRequestException>(() => store.Open("https://example.com/file.xaml", 1, "<A/>"));
        Assert.Throws<LspRequestException>(() => store.Open("file:///A.xaml", 1, "123456789"));
        store.Open("file:///A.xaml", 1, "<A/>");
        Assert.Throws<LspRequestException>(() => store.Open("file:///B.xaml", 1, "<B/>"));
    }
}
