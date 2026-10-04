using System.Text.Json;
using System.Threading.Channels;
using XamlG.LanguageServer.Diagnostics;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class DiagnosticRefreshTests
{
    [Fact]
    public async Task RefreshOwnsOneRequestAndCoalescesSignalsUntilAcknowledged()
    {
        var sent = Channel.CreateUnbounded<JsonElement>();
        await using var refresh = new LspDiagnosticRefreshQueue((value, _) =>
        { sent.Writer.TryWrite(JsonSerializer.SerializeToElement(value)); return ValueTask.CompletedTask; }, debounce: TimeSpan.Zero);
        refresh.Signal();
        var first = await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++) refresh.Signal();
        Assert.False(sent.Reader.TryRead(out _));
        Assert.False(refresh.AcceptResponse(JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = "unknown", result = (object?)null })));
        Assert.True(refresh.AcceptResponse(JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = first.GetProperty("id").GetString(), result = (object?)null })));
        var second = await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        Assert.False(refresh.AcceptResponse(JsonSerializer.SerializeToElement(new { id = first.GetProperty("id").GetString(), result = (object?)null })));
        Assert.True(refresh.AcceptResponse(JsonSerializer.SerializeToElement(new { id = second.GetProperty("id").GetString(), error = new { code = -32601, message = "not supported" } })));
    }
    [Fact]
    public async Task MissingRepliesExpireAndDoNotBlockNewSignals()
    {
        var sent = Channel.CreateUnbounded<JsonElement>(); var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var refresh = new LspDiagnosticRefreshQueue((value, _) =>
        { sent.Writer.TryWrite(JsonSerializer.SerializeToElement(value)); return ValueTask.CompletedTask; },
            debounce: TimeSpan.Zero, replyTimeout: TimeSpan.FromMilliseconds(40), report: _ => expired.TrySetResult());
        refresh.Signal(); await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        refresh.Signal(); await sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task DisposalCancelsTheOutstandingReplyWithoutWaitingForItsDeadline()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = new LspDiagnosticRefreshQueue((_, _) => { started.TrySetResult(); return ValueTask.CompletedTask; }, debounce: TimeSpan.Zero);
        refresh.Signal(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await refresh.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        refresh.Signal(); await refresh.DisposeAsync();
    }
}
