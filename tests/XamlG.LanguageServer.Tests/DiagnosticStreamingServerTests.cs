using Microsoft.CodeAnalysis.CSharp;
using XamlG.Tooling;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class DiagnosticStreamingServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static XamlCompilationSession Compiler() => new(CSharpCompilation.Create("StreamingServer"));

    [Theory]
    [InlineData("client", -32800)]
    [InlineData("buffer", -32802)]
    [InlineData("project", -32802)]
    public async Task ACancelledOrSupersededStreamFinishesItsAdmittedFrameAndRejectsLaterBatches(string cause, int expectedError)
    {
        using var input = new QueuedServerInputStream();
        using var output = new DiagnosticStreamingOutputStream();
        await using var server = new XamlLanguageServer(Compiler(), input, output);
        var run = Task.Run(() => server.RunAsync());
        try
        {
            input.Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            { capabilities = new { textDocument = new { diagnostic = new { relatedDocumentSupport = false } } } } });
            Assert.Equal(1, (await output.ReadMessageAsync()).GetProperty("id").GetInt32());
            for (var i = 0; i < 70; i++)
                input.Send(new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new
                { uri = "untitled:Document" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), languageId = "xaml", version = 1, text = "<Broken" } } });
            input.Send(new { jsonrpc = "2.0", id = 2, method = "workspace/diagnostic", @params = new { partialResultToken = "paused-stream" } });
            await output.Started.Task.WaitAsync(Timeout);
            var first = await output.ReadMessageAsync();
            Assert.Equal("$/progress", first.GetProperty("method").GetString());
            Assert.Equal(32, first.GetProperty("params").GetProperty("value").GetProperty("items").GetArrayLength());
            await input.WaitForPendingReadAsync().WaitAsync(Timeout);
            if (cause == "client") input.Send(new { jsonrpc = "2.0", method = "$/cancelRequest", @params = new { id = 2 } });
            else if (cause == "buffer") input.Send(new { jsonrpc = "2.0", method = "textDocument/didChange", @params = new
            { textDocument = new { uri = "untitled:Document0069", version = 2 }, contentChanges = new[] { new { text = "<Changed" } } } });
            else server.UpdateCompilation(Compiler());
            Assert.False(output.AdmittedCancellation.IsCancellationRequested);
            output.Release.TrySetResult();
            var terminal = await output.ReadMessageAsync();
            Assert.Equal(2, terminal.GetProperty("id").GetInt32());
            Assert.Equal(expectedError, terminal.GetProperty("error").GetProperty("code").GetInt32());
            if (expectedError == -32802) Assert.True(terminal.GetProperty("error").GetProperty("data").GetProperty("retriggerRequest").GetBoolean());
            Assert.False(terminal.TryGetProperty("result", out _));
            input.Send(new { jsonrpc = "2.0", id = 3, method = "workspace/diagnostic", @params = new { } });
            var retry = await output.ReadMessageAsync();
            Assert.Equal(3, retry.GetProperty("id").GetInt32());
            Assert.Equal(70, retry.GetProperty("result").GetProperty("items").GetArrayLength());
            input.Send(new { jsonrpc = "2.0", id = 4, method = "shutdown" });
            Assert.Equal(4, (await output.ReadMessageAsync()).GetProperty("id").GetInt32());
            input.Complete();
            await run.WaitAsync(Timeout);
            // Decode the real Content-Length stream independently; no cancellation tore a frame.
            await using var frames = new LspConnection(new MemoryStream(output.Snapshot()), Stream.Null);
            var count = 0;
            while (await frames.ReadAsync() is { } frame) { using (frame) count++; }
            Assert.Equal(5, count); // initialize, one progress, error, retry, shutdown
        }
        finally
        {
            output.Release.TrySetResult(); input.Complete();
            await run.WaitAsync(Timeout);
        }
    }
}
