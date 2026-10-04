using System.Collections.Immutable;
using System.Text.Json;
using XamlG.LanguageServer.Diagnostics;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class DiagnosticCacheTests
{
    private static ImmutableArray<LspDiagnosticItem> Items(string text = "problem", int line = 0) =>
        ImmutableArray.Create(new LspDiagnosticItem(new(new(line, 0), new(line, 1)), 1, "XG1005", "XamlG", text));
    [Fact]
    public void EquivalentResultsReuseTokensAndUnchangedOmitsItems()
    {
        var cache = new LspDiagnosticCache();
        var first = cache.Report("file:///A.axaml", Items());
        var next = cache.Report("file:///A.axaml", Items(), first.ResultId);
        Assert.Equal("full", first.Kind); Assert.Equal("unchanged", next.Kind);
        Assert.Equal(first.ResultId, next.ResultId); Assert.Null(next.Items);
        var json = JsonSerializer.SerializeToElement(next, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.False(json.TryGetProperty("items", out _)); Assert.False(json.TryGetProperty("relatedDocuments", out _));
    }
    [Fact]
    public void ChangedRangesOrMessagesInvalidateThePreviousReport()
    {
        var cache = new LspDiagnosticCache(); var first = cache.Report("a", Items());
        var moved = cache.Report("a", Items(line: 1), first.ResultId);
        Assert.Equal("full", moved.Kind); Assert.NotEqual(first.ResultId, moved.ResultId);
        Assert.Equal("full", cache.Report("a", Items("changed", 1), moved.ResultId).Kind);
    }
    [Fact]
    public void ForeignUnknownEvictedAndClearedTokensRequireFullReports()
    {
        var cache = new LspDiagnosticCache(capacity: 1);
        var first = cache.Report("a", Items());
        Assert.Equal("full", cache.Report("b", Items(), first.ResultId).Kind);
        Assert.Equal("full", cache.Report("a", Items(), first.ResultId).Kind);
        var token = cache.Report("a", Items()).ResultId;
        Assert.Equal("full", cache.Report("a", Items(), "unknown").Kind);
        cache.Clear(); Assert.Equal(0, cache.Count); Assert.Equal(0, cache.RetainedCharacters);
        Assert.Equal("full", cache.Report("a", Items(), token).Kind);
    }
    [Fact]
    public void OversizedReportsAreNotRetainedAndAdvertiseNoResultId()
    {
        var cache = new LspDiagnosticCache(maximumCharacters: 100);
        var report = cache.Report("a", Items(new string('x', 200)));
        Assert.Equal("full", report.Kind); Assert.Null(report.ResultId); Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.RetainedCharacters);
    }
    [Fact]
    public void EvictionUsesLeastRecentlyUsedOrderAndRespectsBothBudgets()
    {
        var cache = new LspDiagnosticCache(capacity: 2, maximumCharacters: 256);
        var a = cache.Report("a", Items()); var b = cache.Report("b", Items());
        cache.Report("a", Items(), a.ResultId); cache.Report("c", Items());
        Assert.Equal("unchanged", cache.Report("a", Items(), a.ResultId).Kind);
        Assert.Equal("full", cache.Report("b", Items(), b.ResultId).Kind);
        Assert.InRange(cache.Count, 0, 2); Assert.InRange(cache.RetainedCharacters, 0, 256);
    }
    [Fact]
    public async Task ConcurrentIdenticalRequestsPublishOneResultIdentity()
    {
        var cache = new LspDiagnosticCache();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => cache.Report("a", Items()))));
        Assert.Single(results.Select(r => r.ResultId).Distinct()); Assert.Equal(1, cache.Count);
    }
    [Fact]
    public void CancellationCannotEvictAnExistingReport()
    {
        var cache = new LspDiagnosticCache(); var first = cache.Report("a", Items());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => cache.Report("a", Items("new"), cancellationToken: cancellation.Token));
        Assert.Equal("unchanged", cache.Report("a", Items(), first.ResultId).Kind);
    }
}
