using System.Text;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;

namespace XamlG.Agents.Gemini;

/// <summary>Official Google Gen AI SDK adapter with native parts and thought signatures
/// preserved across function calls. Automatic SDK retries are disabled per generation;
/// the harness accounts for each attempt and executes all local tools.</summary>
public sealed class GeminiAgentProvider(Client client) : IAgentProvider
{
    public string Id => "gemini";

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = new List<string>();
            var pages = await client.Models.ListAsync(new() { HttpOptions = NoRetries() }, cancellationToken);
            for (var index = 0; ; index++)
            {
                result.AddRange(pages.CurrentPage.Where(model => !string.IsNullOrWhiteSpace(model.Name)).Select(model => model.Name!));
                if (result.Count > 10000 || index >= 100) throw new AgentProviderException("model_catalog_too_large", false);
                if (!await pages.NextPageAsync()) break;
                cancellationToken.ThrowIfCancellationRequested();
            }
            return result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (ApiException error) { throw HttpError(error); }
        catch (HttpRequestException) { throw new AgentProviderException("connection_error", true); }
        catch (JsonException) { throw ProtocolError(); }
    }

    public int GetContextBytes(AgentRequest request)
    {
        var (contents, config) = Options(request);
        return JsonSerializer.SerializeToUtf8Bytes(new { model = request.Model, contents, config }).Length;
    }

    public async Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> textDelta, CancellationToken cancellationToken)
    {
        var (contents, config) = Options(request);
        var parts = new List<Part>(); var text = new StringBuilder();
        GenerateContentResponseUsageMetadata? usage = null;
        FinishReason? finish = null; var bytes = 0;
        try
        {
            await foreach (var response in client.Models.GenerateContentStreamAsync(request.Model, contents, config, cancellationToken))
            {
                bytes = checked(bytes + JsonSerializer.SerializeToUtf8Bytes(response).Length);
                if (bytes > 16 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
                usage = response.UsageMetadata ?? usage;
                if (response.PromptFeedback?.BlockReason is { } blocked && blocked.Value != "BLOCK_REASON_UNSPECIFIED")
                    throw new AgentProviderException("safety_rejected", false, canResume: false);
                if (response.Candidates is not { Count: > 0 } candidates) continue;
                if (candidates.Count != 1 || candidates[0].Index is not (null or 0)) throw ProtocolError();
                var candidate = candidates[0];
                if (finish != null) throw ProtocolError();
                if (candidate.Content != null)
                {
                    if (candidate.Content.Role is not (null or "model")) throw ProtocolError();
                    foreach (var part in candidate.Content.Parts ?? [])
                    {
                        if (parts.Count >= 10000) throw new AgentProviderException("response_too_large", false);
                        parts.Add(part);
                        if (part.Text is { Length: > 0 } delta && part.Thought != true)
                        { text.Append(delta); await textDelta(delta); }
                    }
                }
                finish = candidate.FinishReason;
            }
        }
        catch (ApiException error) { throw HttpError(error); }
        catch (HttpRequestException) { throw new AgentProviderException("connection_error", true); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        { throw ProtocolError(); }
        if (finish == null) throw new AgentProviderException("stream_ended_without_terminal_event", true);
        if (finish != FinishReason.Stop && finish != FinishReason.MaxTokens)
            throw new AgentProviderException(finish == FinishReason.MalformedFunctionCall ? "invalid_tool_arguments" : "generation_rejected", false, canResume: false);
        var calls = new List<AgentToolCall>(); var nativeCalls = new Dictionary<string, FunctionCall>(StringComparer.Ordinal);
        if (finish != FinishReason.MaxTokens)
            foreach (var part in parts)
            {
                if (part.FunctionCall is not { } call) continue;
                if (string.IsNullOrWhiteSpace(call.Name) || call.PartialArgs?.Count > 0 || call.WillContinue == true) throw ProtocolError();
                var id = call.Id ?? "call_" + Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(id) || !nativeCalls.TryAdd(id, call)) throw ProtocolError();
                calls.Add(new(id, call.Name, JsonSerializer.SerializeToElement(call.Args ?? [])));
            }
        var nativeContent = new Content { Role = "model", Parts = parts };
        var nativeBytes = JsonSerializer.SerializeToUtf8Bytes(nativeContent).Length;
        if (nativeBytes > 8 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
        AgentUsage usageResult;
        if (usage?.PromptTokenCount is { } input && (usage.TotalTokenCount.HasValue || usage.CandidatesTokenCount.HasValue))
        {
            var inputCount = checked((long)input + (usage.ToolUsePromptTokenCount ?? 0));
            var outputCount = usage.TotalTokenCount is { } total ? total - inputCount : checked((long)usage.CandidatesTokenCount!.Value + (usage.ThoughtsTokenCount ?? 0));
            if (inputCount < 0 || outputCount < 0) throw ProtocolError();
            usageResult = new(inputCount, outputCount);
        }
        else usageResult = new((GetContextBytes(request) + 3L) / 4, (nativeBytes + 3L) / 4, true);
        return new(text.ToString(), calls, usageResult, new NativeMessage(nativeContent, nativeCalls), finish == FinishReason.MaxTokens);
    }

    private static (List<Content> Contents, GenerateContentConfig Config) Options(AgentRequest request)
    {
        var contents = new List<Content>(); var results = new List<Part>();
        IReadOnlyDictionary<string, FunctionCall> nativeCalls = new Dictionary<string, FunctionCall>();
        void FlushResults()
        {
            if (results.Count == 0) return;
            contents.Add(new() { Role = "user", Parts = results.ToList() }); results.Clear();
        }
        foreach (var message in request.Messages)
        {
            if (message.Kind == AgentMessageKind.ToolResult)
            {
                if (message.ToolCallId == null || !nativeCalls.TryGetValue(message.ToolCallId, out var call))
                    throw new InvalidOperationException("Tool result does not match a native function call.");
                using var value = JsonDocument.Parse(message.Text);
                var response = value.RootElement.ValueKind == JsonValueKind.Object ? value.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => (object)item.Value.Clone()) :
                    new Dictionary<string, object> { ["output"] = value.RootElement.Clone() };
                results.Add(new() { FunctionResponse = new() { Name = call.Name, Id = call.Id, Response = response } });
                continue;
            }
            FlushResults();
            if (message.Kind == AgentMessageKind.User) contents.Add(new() { Role = "user", Parts = [Part.FromText(message.Text)] });
            else if (message.Native is NativeMessage native) { contents.Add(native.Content); nativeCalls = native.Calls; }
            else throw new InvalidOperationException("The task contains a different provider's native context.");
        }
        FlushResults();
        return (contents, new()
        {
            SystemInstruction = new() { Parts = [Part.FromText(request.Instructions)] }, MaxOutputTokens = request.MaxOutputTokens,
            CandidateCount = 1, HttpOptions = NoRetries(),
            Tools = request.Tools.Count == 0 ? null : [new() { FunctionDeclarations = request.Tools.Select(tool => new FunctionDeclaration
            { Name = tool.Name, Description = tool.Description, ParametersJsonSchema = tool.InputSchema }).ToList() }]
        });
    }

    private static HttpOptions NoRetries() => new() { RetryOptions = new() { Attempts = 1 } };
    private static AgentProviderException ProtocolError() => new("invalid_response_protocol", false);
    private static AgentProviderException HttpError(ApiException error) => new("http_" + error.StatusCode, error.StatusCode is 408 or 429 or >= 500);
    private sealed record NativeMessage(Content Content, IReadOnlyDictionary<string, FunctionCall> Calls);
}
