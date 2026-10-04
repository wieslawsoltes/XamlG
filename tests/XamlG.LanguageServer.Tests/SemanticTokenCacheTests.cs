using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class SemanticTokenCacheTests
{
    private static readonly int[] First = { 0, 1, 4, 2, 0, 1, 2, 5, 3, 0, 2, 3, 6, 4, 0 };
    [Fact]
    public void DeltasRoundTripCompleteFiveIntegerRecords()
    {
        var cache = new LspSemanticTokenCache();
        var before = Assert.IsType<LspSemanticTokensFull>(cache.GetResult("file:///A.xaml", First));
        var next = new[] { 0, 1, 4, 2, 0, 1, 2, 7, 3, 0, 1, 1, 3, 2, 0, 2, 3, 6, 4, 0 };
        var delta = Assert.IsType<LspSemanticTokensDelta>(cache.GetResult("file:///A.xaml", next, before.ResultId));
        var edit = Assert.Single(delta.Edits);
        Assert.Equal(5, edit.Start); Assert.Equal(5, edit.DeleteCount); Assert.Equal(10, edit.Data.Length);
        Assert.Equal(next, before.Data.Take(edit.Start).Concat(edit.Data).Concat(before.Data.Skip(edit.Start + edit.DeleteCount)));
        var unchanged = Assert.IsType<LspSemanticTokensDelta>(cache.GetResult("file:///A.xaml", next, delta.ResultId));
        Assert.Empty(unchanged.Edits); Assert.Equal(delta.ResultId, unchanged.ResultId);
    }
    [Fact]
    public void ForeignAndEvictedResultIdsReturnFullData()
    {
        var cache = new LspSemanticTokenCache(maximumDocuments: 1, maximumIntegers: 30);
        var a = Assert.IsType<LspSemanticTokensFull>(cache.GetResult("A", First));
        var b = Assert.IsType<LspSemanticTokensFull>(cache.GetResult("B", First, a.ResultId));
        Assert.NotEqual(a.ResultId, b.ResultId);
        Assert.IsType<LspSemanticTokensFull>(cache.GetResult("A", First, a.ResultId));
        Assert.True(cache.RetainedIntegers <= 30);
        cache.Remove("A"); Assert.Equal(0, cache.RetainedIntegers);
    }
    [Fact]
    public void CallersCannotMutateCachedDataAndHistoryIsBounded()
    {
        var cache = new LspSemanticTokenCache(maximumIntegers: 30);
        var source = (int[])First.Clone();
        var first = Assert.IsType<LspSemanticTokensFull>(cache.GetResult("A", source));
        first.Data[0] = 999; source[0] = 888;
        var same = Assert.IsType<LspSemanticTokensDelta>(cache.GetResult("A", First, first.ResultId));
        Assert.Empty(same.Edits);
        for (var i = 0; i < 20; i++)
        {
            var next = (int[])First.Clone(); next[1] = i + 2;
            cache.GetResult("A", next);
            Assert.True(cache.RetainedIntegers <= 30);
        }
        Assert.IsType<LspSemanticTokensFull>(cache.GetResult("A", First, first.ResultId));
    }
    [Fact]
    public void EmptyAndOversizedResponsesDoNotBreakBudgets()
    {
        var cache = new LspSemanticTokenCache(maximumIntegers: 5);
        var empty = Assert.IsType<LspSemanticTokensFull>(cache.GetResult("A", Array.Empty<int>()));
        var inserted = Assert.IsType<LspSemanticTokensDelta>(cache.GetResult("A", First, empty.ResultId));
        Assert.Equal(First, Assert.Single(inserted.Edits).Data);
        Assert.Equal(0, cache.RetainedIntegers);
        Assert.IsType<LspSemanticTokensFull>(cache.GetResult("A", First, inserted.ResultId));
        Assert.Throws<ArgumentException>(() => cache.GetResult("A", new[] { 1, 2, 3 }));
        Assert.Throws<ArgumentException>(() => cache.GetResult("A", new[] { -1, 0, 1, 2, 0 }));
    }
}
