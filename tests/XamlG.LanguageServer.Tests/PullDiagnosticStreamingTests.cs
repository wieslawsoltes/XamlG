using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.LanguageServer.Diagnostics;
using XamlG.Tooling;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class PullDiagnosticStreamingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static JsonElement Wire(object? value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private static (LspWorkspaceAnalysis Workspace, LspDocumentSetSnapshot Buffers) Workspace(int count)
    {
        var store = new LspDocumentStore();
        for (var index = 0; index < count; index++)
            store.Open("untitled:Document" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), index + 1, "<Broken");
        var buffers = store.Capture();
        var compiler = new XamlCompilationSession(CSharpCompilation.Create("DiagnosticStreaming"));
        return (new(compiler, compiler.AnalyzeWorkspace(buffers.Documents.Select(document => document.Syntax))), buffers);
    }
    private static Func<object, CancellationToken, ValueTask> Capture(List<JsonElement> values) => (message, _) =>
    { values.Add(Wire(message).GetProperty("params").GetProperty("value")); return ValueTask.CompletedTask; };

    [Fact]
    public async Task StreamedWorkspaceEqualsTheFullReplyWithoutDuplicatesAndReusesResultIds()
    {
        var (workspace, buffers) = Workspace(70);
        var cache = new LspDiagnosticCache();
        var handler = new LspPullDiagnosticRequests(cache, false);
        var progress = new List<JsonElement>();
        var full = Wire(await handler.HandleAsync(LspDiagnosticMethods.Workspace, Wire(new { previousResultIds = Array.Empty<object>() }),
            workspace, buffers, Capture(progress), default));
        Assert.Empty(progress);
        var terminal = Wire(await handler.HandleAsync(LspDiagnosticMethods.Workspace, Wire(new { partialResultToken = 0, previousResultIds = Array.Empty<object>() }),
            workspace, buffers, Capture(progress), default));
        Assert.Equal(new[] { 32, 32, 6 }, progress.Select(value => value.GetProperty("items").GetArrayLength()));
        Assert.Empty(terminal.GetProperty("items").EnumerateArray());
        var streamed = progress.SelectMany(value => value.GetProperty("items").EnumerateArray()).ToArray();
        Assert.Equal(full.GetProperty("items").EnumerateArray().Select(item => item.GetRawText()), streamed.Select(item => item.GetRawText()));
        Assert.Equal(70, streamed.Select(item => item.GetProperty("uri").GetString()).Distinct().Count());
        Assert.All(streamed, item => Assert.NotEmpty(item.GetProperty("items").EnumerateArray()));
        Assert.Equal(70, cache.Count);

        var previous = streamed.Select(item => new { uri = item.GetProperty("uri").GetString(), value = item.GetProperty("resultId").GetString() })
            .Append(new { uri = (string?)"untitled:Removed", value = (string?)"unknown" }).ToArray();
        progress.Clear();
        await handler.HandleAsync(LspDiagnosticMethods.Workspace, Wire(new { partialResultToken = "unchanged", previousResultIds = previous }),
            workspace, buffers, Capture(progress), default);
        var reports = progress.SelectMany(value => value.GetProperty("items").EnumerateArray()).ToArray();
        Assert.All(reports.Take(70), item =>
        { Assert.Equal("unchanged", item.GetProperty("kind").GetString()); Assert.False(item.TryGetProperty("items", out _)); });
        Assert.Equal("untitled:Removed", reports[^1].GetProperty("uri").GetString());
        Assert.Equal(JsonValueKind.Null, reports[^1].GetProperty("version").ValueKind);
        Assert.Empty(reports[^1].GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task DocumentProgressStartsWithThePrimaryReportAndCompletesWithoutRepeatingIt()
    {
        var (workspace, buffers) = Workspace(1);
        var handler = new LspPullDiagnosticRequests(new(), false);
        var values = new List<JsonElement>();
        var parameters = new { textDocument = new { uri = "untitled:Document0000" }, partialResultToken = "" };
        var result = await handler.HandleAsync(LspDiagnosticMethods.Document, Wire(parameters), workspace, buffers, Capture(values), default);
        Assert.Null(result);
        var primary = Assert.Single(values);
        Assert.Equal("full", primary.GetProperty("kind").GetString());
        Assert.NotEmpty(primary.GetProperty("items").EnumerateArray());
        Assert.False(primary.TryGetProperty("relatedDocuments", out _));
        values.Clear();
        var repeat = new { parameters.textDocument, partialResultToken = "", previousResultId = primary.GetProperty("resultId").GetString() };
        Assert.Null(await handler.HandleAsync(LspDiagnosticMethods.Document, Wire(repeat), workspace, buffers, Capture(values), default));
        Assert.Equal("unchanged", Assert.Single(values).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task AllRequestParametersAreValidatedBeforeCacheMutationOrProgress()
    {
        var (workspace, buffers) = Workspace(70);
        var cache = new LspDiagnosticCache(); var handler = new LspPullDiagnosticRequests(cache, true);
        var values = new List<JsonElement>();
        var previous = Enumerable.Range(0, 65).Select(index => new { uri = "untitled:Old" + index, value = "" })
            .Append(new { uri = "https://invalid.example/document", value = "" }).ToArray();
        var error = await Assert.ThrowsAsync<LspRequestException>(() => handler.HandleAsync(LspDiagnosticMethods.Workspace,
            Wire(new { partialResultToken = "late-invalid", previousResultIds = previous }), workspace, buffers, Capture(values), default).AsTask());
        Assert.Equal(-32602, error.Code); Assert.Empty(values); Assert.Equal(0, cache.Count);
        await Assert.ThrowsAsync<LspRequestException>(() => handler.HandleAsync(LspDiagnosticMethods.Document,
            Wire(new { textDocument = new { uri = "untitled:Document0000" }, partialResultToken = (object?)null }),
            workspace, buffers, Capture(values), default).AsTask());
        Assert.Empty(values); Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task CancellationStopsReportProjectionAndTheNextPullRecovers()
    {
        var (workspace, buffers) = Workspace(70);
        var cache = new LspDiagnosticCache(); var handler = new LspPullDiagnosticRequests(cache, false);
        using var cancellation = new CancellationTokenSource();
        var sent = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(LspDiagnosticMethods.Workspace,
            Wire(new { partialResultToken = 17 }), workspace, buffers, (_, _) =>
            { sent++; cancellation.Cancel(); return ValueTask.CompletedTask; }, cancellation.Token).AsTask());
        Assert.Equal(1, sent); Assert.Equal(32, cache.Count);
        var recovered = Wire(await handler.HandleAsync(LspDiagnosticMethods.Workspace, Wire(new { }), workspace, buffers,
            (_, _) => throw new InvalidOperationException("Non-streaming requests must not publish progress."), default));
        Assert.Equal(70, recovered.GetProperty("items").GetArrayLength()); Assert.Equal(70, cache.Count);
    }

    [Fact]
    public async Task EmptyWorkspaceReturnsAnEmptyResultAndDoesNotInventAProgressFrame()
    {
        var (workspace, buffers) = Workspace(0);
        var values = new List<JsonElement>();
        var result = Wire(await new LspPullDiagnosticRequests(new(), false).HandleAsync(LspDiagnosticMethods.Workspace,
            Wire(new { partialResultToken = 0 }), workspace, buffers, Capture(values), default));
        Assert.Empty(values); Assert.Empty(result.GetProperty("items").EnumerateArray());
    }
}
