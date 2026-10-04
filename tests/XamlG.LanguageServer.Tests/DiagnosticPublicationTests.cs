using System.Collections.Immutable;
using XamlG.LanguageServer.Diagnostics;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class DiagnosticPublicationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static ImmutableArray<LspDiagnosticItem> Items(string message) => ImmutableArray.Create(
        new LspDiagnosticItem(new(new(0, 0), new(0, 1)), 1, "XG3305", "XamlG", message));

    [Fact]
    public async Task CachedReportsCannotBypassTheCompleteBufferPublicationGate()
    {
        var store = new LspDocumentStore();
        store.Open("file:///Caller.axaml", 1, "<Root/>");
        store.Open("file:///Resource.axaml", 1, "<Resource/>");
        var previous = store.Capture(); var cache = new LspDiagnosticCache();
        var old = cache.Report("file:///Caller.axaml", Items("old dependency"));
        using var output = new ControlledOutputStream();
        await using var connection = new LspConnection(Stream.Null, output);
        var blocker = connection.WriteAsync(new { jsonrpc = "2.0", id = 1, result = "occupy output" }).AsTask();
        try
        {
            await output.Started.Task.WaitAsync(Timeout);
            var queued = connection.TryWriteAsync(new { jsonrpc = "2.0", id = 2, result = old }, () => store.IsCurrent(previous)).AsTask();
            store.Change("file:///Resource.axaml", 2, new[] { new LspTextChange(null, "<Changed/>") });
            Assert.Equal(1, store.Get("file:///Caller.axaml").Version);
            output.Release.TrySetResult();
            await blocker.WaitAsync(Timeout);
            Assert.False(await queued.WaitAsync(Timeout));
            var current = store.Capture();
            var latest = cache.Report("file:///Caller.axaml", Items("new dependency"), old.ResultId);
            Assert.True(await connection.TryWriteAsync(new { jsonrpc = "2.0", id = 3, result = latest }, () => store.IsCurrent(current)));
            await using var reader = new LspConnection(new MemoryStream(output.Snapshot()), Stream.Null);
            using var first = await reader.ReadAsync(); using var next = await reader.ReadAsync();
            Assert.Equal(1, first!.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(3, next!.RootElement.GetProperty("id").GetInt32());
            Assert.Equal("new dependency", next.RootElement.GetProperty("result").GetProperty("items")[0].GetProperty("message").GetString());
            Assert.Null(await reader.ReadAsync()); Assert.Null(connection.Failure);
        }
        finally { output.Release.TrySetResult(); }
    }

    [Fact]
    public async Task CancelledQueuedPullDoesNotDamageASiblingReportOrItsCacheIdentity()
    {
        var cache = new LspDiagnosticCache(); var report = cache.Report("a", Items("shared result"));
        using var output = new ControlledOutputStream();
        await using var connection = new LspConnection(Stream.Null, output);
        using var cancelled = new CancellationTokenSource();
        var blocker = connection.WriteAsync(new { jsonrpc = "2.0", id = 1, result = "occupy output" }).AsTask();
        try
        {
            await output.Started.Task.WaitAsync(Timeout);
            var withdrawn = connection.TryWriteAsync(new { jsonrpc = "2.0", id = 2, result = report }, () => true, cancelled.Token).AsTask();
            var sibling = connection.TryWriteAsync(new { jsonrpc = "2.0", id = 3, result = report }, () => true).AsTask();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn);
            Assert.False(sibling.IsCompleted);
            output.Release.TrySetResult(); await blocker.WaitAsync(Timeout);
            Assert.True(await sibling.WaitAsync(Timeout));
            Assert.Equal("unchanged", cache.Report("a", Items("shared result"), report.ResultId).Kind);
            await using var reader = new LspConnection(new MemoryStream(output.Snapshot()), Stream.Null);
            using var first = await reader.ReadAsync(); using var second = await reader.ReadAsync();
            Assert.Equal(1, first!.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(3, second!.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(report.ResultId, second.RootElement.GetProperty("result").GetProperty("resultId").GetString());
            Assert.Null(await reader.ReadAsync()); Assert.Null(connection.Failure);
        }
        finally { output.Release.TrySetResult(); }
    }
}
