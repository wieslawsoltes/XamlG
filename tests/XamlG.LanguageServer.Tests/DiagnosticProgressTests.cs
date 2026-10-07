using System.Collections.Immutable;
using System.Text.Json;
using XamlG.LanguageServer.Diagnostics;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class DiagnosticProgressTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static JsonElement Json(string text) { using var value = JsonDocument.Parse(text); return value.RootElement.Clone(); }
    private static LspWorkspaceDocumentDiagnosticReport Report(int index, string message = "") => new(
        "untitled:Document" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), index, "full", "result" + index)
        { Items = ImmutableArray.Create(new LspDiagnosticItem(new(new(0, 0), new(0, 1)), 1, "XG1005", "XamlG", message)) };

    [Theory]
    [InlineData("0")]
    [InlineData("-2147483648")]
    [InlineData("2147483647")]
    [InlineData("\"\"")]
    [InlineData("\"stream/Zażółć 😀\"")]
    public void ProgressTokensPreserveTheirWireTypeAndSurviveJsonDocumentDisposal(string literal)
    {
        JsonElement? result;
        using (var parameters = JsonDocument.Parse("{\"partialResultToken\":" + literal + "}"))
            result = LspDiagnosticProgress.ReadToken(parameters.RootElement);
        Assert.NotNull(result);
        Assert.Equal(literal, result.Value.GetRawText());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public void InvalidTokensAreInvalidParameters(string literal)
    {
        var error = Assert.Throws<LspRequestException>(() => LspDiagnosticProgress.ReadToken(Json("{\"partialResultToken\":" + literal + "}")));
        Assert.Equal(-32602, error.Code);
    }

    [Fact]
    public void TokenLengthAndParameterShapeAreBoundedWithoutRejectingAnAbsentToken()
    {
        Assert.Null(LspDiagnosticProgress.ReadToken(Json("{}")));
        var accepted = "{\"partialResultToken\":\"" + new string('x', 1024) + "\"}";
        Assert.NotNull(LspDiagnosticProgress.ReadToken(Json(accepted)));
        Assert.Throws<LspRequestException>(() => LspDiagnosticProgress.ReadToken(Json(accepted.Replace("xxx", "xxxx", StringComparison.Ordinal))));
        Assert.Throws<LspRequestException>(() => LspDiagnosticProgress.ReadToken(Json("[]")));
    }

    [Fact]
    public async Task WorkspaceBatchesAreBoundedOrderedAndNotDuplicated()
    {
        var messages = new List<JsonElement>();
        var progress = new LspDiagnosticProgress(Json("0"), (message, _) =>
        { messages.Add(JsonSerializer.SerializeToElement(message, JsonOptions)); return ValueTask.CompletedTask; });
        await progress.WriteWorkspaceAsync(Enumerable.Range(0, 67).Select(index => Report(index)), default);
        Assert.Equal(new[] { 32, 32, 3 }, messages.Select(message => message.GetProperty("params").GetProperty("value").GetProperty("items").GetArrayLength()));
        var documents = messages.SelectMany(message => message.GetProperty("params").GetProperty("value").GetProperty("items").EnumerateArray()).ToArray();
        Assert.Equal(Enumerable.Range(0, 67).Select(index => Report(index).Uri), documents.Select(item => item.GetProperty("uri").GetString()));
        Assert.All(messages, message =>
        {
            Assert.Equal("$/progress", message.GetProperty("method").GetString());
            Assert.Equal(0, message.GetProperty("params").GetProperty("token").GetInt32());
            Assert.False(message.TryGetProperty("id", out _));
        });
    }

    [Fact]
    public async Task Utf8BatchSizingIncludesEscapesAndKeepsOversizedSingleReportsIntact()
    {
        var messages = new List<byte[]>();
        var progress = new LspDiagnosticProgress(Json("\"😀\\\"\\n\""), (message, _) =>
        { messages.Add(JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions)); return ValueTask.CompletedTask; });
        var text = new string('ż', 30_000); // The wire encoder escapes each character.
        await progress.WriteWorkspaceAsync(new[] { Report(1, text), Report(2, text), Report(3, text + text) }, default);
        Assert.Equal(3, messages.Count);
        Assert.True(messages[0].Length <= LspDiagnosticProgress.TargetPayloadBytes);
        Assert.True(messages[1].Length <= LspDiagnosticProgress.TargetPayloadBytes);
        Assert.True(messages[2].Length > LspDiagnosticProgress.TargetPayloadBytes);
        using var last = JsonDocument.Parse(messages[2]);
        Assert.Equal(text + text, last.RootElement.GetProperty("params").GetProperty("value").GetProperty("items")[0].GetProperty("items")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task RelatedBatchesUseUriMapsWithoutInventingPrimaryReports()
    {
        var messages = new List<JsonElement>();
        var progress = new LspDiagnosticProgress(Json("\"related\""), (message, _) =>
        { messages.Add(JsonSerializer.SerializeToElement(message, JsonOptions)); return ValueTask.CompletedTask; });
        var items = Enumerable.Range(0, 35).Select(index => new KeyValuePair<string, LspDocumentDiagnosticReport>(
            "untitled:Related" + index, new("full", "r" + index) { Items = ImmutableArray<LspDiagnosticItem>.Empty }));
        await progress.WriteRelatedAsync(items, default);
        Assert.Equal(new[] { 32, 3 }, messages.Select(message => message.GetProperty("params").GetProperty("value").GetProperty("relatedDocuments").EnumerateObject().Count()));
        Assert.All(messages, message => Assert.False(message.GetProperty("params").GetProperty("value").TryGetProperty("kind", out _)));
    }

    [Fact]
    public async Task BackpressureAndCancellationPreventProjectingTheNextBatch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var projected = 0; var disposed = false; var sent = 0;
        IEnumerable<LspWorkspaceDocumentDiagnosticReport> Reports()
        {
            try { for (var i = 0; i < 100; i++) { projected++; yield return Report(i); } }
            finally { disposed = true; }
        }
        var progress = new LspDiagnosticProgress(Json("17"), async (_, _) =>
        { sent++; started.TrySetResult(); await release.Task; });
        var run = progress.WriteWorkspaceAsync(Reports(), cancellation.Token);
        try
        {
            await started.Task.WaitAsync(Timeout);
            Assert.Equal(32, projected); Assert.False(run.IsCompleted);
            cancellation.Cancel(); release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Timeout));
            Assert.Equal(32, projected); Assert.Equal(1, sent); Assert.True(disposed);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task EmptyResultsDoNotEmitSpuriousProgress()
    {
        var sent = 0;
        var progress = new LspDiagnosticProgress(Json("0"), (_, _) => { sent++; return ValueTask.CompletedTask; });
        await progress.WriteWorkspaceAsync(Array.Empty<LspWorkspaceDocumentDiagnosticReport>(), default);
        await progress.WriteRelatedAsync(Array.Empty<KeyValuePair<string, LspDocumentDiagnosticReport>>(), default);
        Assert.Equal(0, sent);
    }
}
