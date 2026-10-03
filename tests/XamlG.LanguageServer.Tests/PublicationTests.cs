using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class PublicationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static object Message(int id) => new { jsonrpc = "2.0", id, result = "😀 complete frame" };

    [Fact]
    public async Task RequestCancellationAfterTheHeaderCannotTruncateItsPayload()
    {
        using var output = new ControlledOutputStream();
        using var request = new CancellationTokenSource();
        await using var connection = new LspConnection(Stream.Null, output);
        var first = connection.WriteAsync(Message(1), request.Token).AsTask();
        await output.Started.Task.WaitAsync(TestTimeout);
        request.Cancel();
        output.Release.SetResult();
        await first.WaitAsync(TestTimeout);
        await connection.WriteAsync(Message(2));
        Assert.Null(connection.Failure);
        Assert.Equal(new[] { 1, 2 }, await ReadIds(output.Snapshot()));
    }

    [Fact]
    public async Task CancelledQueuedMessagesEmitNoBytesAndDoNotPoisonTheConnection()
    {
        using var output = new ControlledOutputStream();
        await using var connection = new LspConnection(Stream.Null, output);
        var first = connection.WriteAsync(Message(1)).AsTask();
        await output.Started.Task.WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var second = connection.WriteAsync(Message(2), cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        output.Release.SetResult();
        await first.WaitAsync(TestTimeout);
        await connection.WriteAsync(Message(3));
        Assert.Equal(new[] { 1, 3 }, await ReadIds(output.Snapshot()));
        Assert.Null(connection.Failure);
    }

    [Fact]
    public async Task FreshnessIsCheckedAfterAcquiringTheOutputGate()
    {
        using var output = new ControlledOutputStream();
        await using var connection = new LspConnection(Stream.Null, output);
        var first = connection.WriteAsync(Message(1)).AsTask();
        await output.Started.Task.WaitAsync(TestTimeout);
        var revision = 1;
        var stale = connection.TryWriteAsync(Message(2), () => Volatile.Read(ref revision) == 1).AsTask();
        Volatile.Write(ref revision, 2);
        output.Release.SetResult();
        await first.WaitAsync(TestTimeout);
        Assert.False(await stale.WaitAsync(TestTimeout));
        Assert.True(await connection.TryWriteAsync(Message(3), () => Volatile.Read(ref revision) == 2));
        Assert.Equal(new[] { 1, 3 }, await ReadIds(output.Snapshot()));
    }

    [Fact]
    public async Task FreshnessFailureDoesNotCorruptTheTransport()
    {
        using var output = new MemoryStream();
        await using var connection = new LspConnection(Stream.Null, output);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.TryWriteAsync(Message(1), () => throw new InvalidOperationException("Predicate failed.")).AsTask());
        Assert.Equal(0, output.Length);
        await connection.WriteAsync(Message(2));
        Assert.Equal(new[] { 2 }, await ReadIds(output.ToArray()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PartialWriteFailurePermanentlyRejectsLaterFrames(int failedWrite)
    {
        using var output = new ControlledOutputStream { BlockWrite = 0, FailWrite = failedWrite };
        await using var connection = new LspConnection(Stream.Null, output);
        await Assert.ThrowsAsync<LspTransportException>(() => connection.WriteAsync(Message(1)).AsTask());
        var written = output.Snapshot();
        await Assert.ThrowsAsync<LspTransportException>(() => connection.WriteAsync(Message(2)).AsTask());
        Assert.Equal(written, output.Snapshot());
        Assert.True(connection.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task FlushFailureAlsoPermanentlyClosesTheConnection()
    {
        using var output = new ControlledOutputStream { BlockWrite = 0, FailFlush = true };
        await using var connection = new LspConnection(Stream.Null, output);
        await Assert.ThrowsAsync<LspTransportException>(() => connection.WriteAsync(Message(1)).AsTask());
        var writes = output.WriteCalls;
        await Assert.ThrowsAsync<LspTransportException>(() => connection.WriteAsync(Message(2)).AsTask());
        Assert.Equal(writes, output.WriteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineIsBoundedEvenWhenTheStreamIgnoresCancellation(bool ignoreCancellation)
    {
        using var output = new ControlledOutputStream { IgnoreCancellation = ignoreCancellation };
        await using var connection = new LspConnection(Stream.Null, output, writeTimeout: TimeSpan.FromMilliseconds(150));
        try
        {
            await Assert.ThrowsAsync<LspTransportException>(() => connection.WriteAsync(Message(1)).AsTask().WaitAsync(TestTimeout));
            Assert.True(connection.Closed.IsCancellationRequested);
            Assert.Equal(1, output.WriteCalls);
            await Assert.ThrowsAsync<LspTransportException>(() => connection.WriteAsync(Message(2)).AsTask());
        }
        finally { output.Release.TrySetResult(); }
    }

    [Fact]
    public async Task DisposalInterruptsABlockedWriteAndCanBeAwaitedRepeatedly()
    {
        using var output = new ControlledOutputStream { IgnoreCancellation = true };
        var connection = new LspConnection(Stream.Null, output);
        var pending = connection.WriteAsync(Message(1)).AsTask();
        await output.Started.Task.WaitAsync(TestTimeout);
        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            await Assert.ThrowsAsync<LspTransportException>(() => pending);
            await connection.DisposeAsync();
            Assert.Equal(1, output.WriteCalls);
        }
        finally { output.Release.TrySetResult(); }
    }

    [Fact]
    public async Task TransportLifetimeCancellationClosesRatherThanResumingAPartialFrame()
    {
        using var output = new ControlledOutputStream();
        using var lifetime = new CancellationTokenSource();
        await using var connection = new LspConnection(Stream.Null, output, lifetimeToken: lifetime.Token);
        var pending = connection.WriteAsync(Message(1)).AsTask();
        await output.Started.Task.WaitAsync(TestTimeout);
        lifetime.Cancel();
        await Assert.ThrowsAsync<LspTransportException>(() => pending);
        await Assert.ThrowsAsync<LspTransportException>(() => connection.WriteAsync(Message(2)).AsTask());
        Assert.Equal(1, output.WriteCalls);
    }

    private static async Task<int[]> ReadIds(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        await using var reader = new LspConnection(stream, Stream.Null);
        var result = new List<int>();
        while (await reader.ReadAsync() is { } message)
        {
            using (message) result.Add(message.RootElement.GetProperty("id").GetInt32());
        }
        return result.ToArray();
    }
}
