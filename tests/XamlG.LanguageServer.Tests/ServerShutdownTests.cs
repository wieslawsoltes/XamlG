using Microsoft.CodeAnalysis.CSharp;
using XamlG.Tooling;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class ServerShutdownTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExitOrEofAfterTheResponseBytesDoesNotCancelItsFinalFlush(bool sendExit)
    {
        using var input = new QueuedServerInputStream();
        using var output = new ShutdownFlushStream();
        var compiler = new XamlCompilationSession(CSharpCompilation.Create("ShutdownTest"));
        await using var server = new XamlLanguageServer(compiler, input, output);
        // No synchronization context: the input fixture completes the reader inline.
        var run = Task.Run(() => server.RunAsync());
        try
        {
            input.Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } });
            await output.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
            input.Send(new { jsonrpc = "2.0", id = 2, method = "shutdown" });
            await output.FinalFlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await input.WaitForPendingReadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (sendExit) input.Send(new { jsonrpc = "2.0", method = "exit" });
            else input.Complete();
            Assert.False(output.FinalFlushCancellation.IsCancellationRequested);
            Assert.False(run.IsCompleted);
            output.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(run.IsCompletedSuccessfully);
            await using var frames = new LspConnection(new MemoryStream(output.Snapshot()), Stream.Null);
            using var initialize = await frames.ReadAsync();
            using var shutdown = await frames.ReadAsync();
            Assert.Equal(1, initialize!.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(2, shutdown!.RootElement.GetProperty("id").GetInt32());
            Assert.False(shutdown.RootElement.TryGetProperty("error", out _));
            Assert.Null(await frames.ReadAsync());
        }
        finally
        {
            output.Release.TrySetResult();
            input.Complete();
        }
    }
}
