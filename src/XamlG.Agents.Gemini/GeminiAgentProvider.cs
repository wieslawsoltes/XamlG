using System.Text;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;

namespace XamlG.Agents.Gemini;

/// <summary>Official Google Gen AI SDK adapter with native parts and thought signatures
/// preserved across function calls. Automatic SDK retries are disabled per generation;
/// the harness accounts for each attempt and executes all local tools.</summary>
public sealed class GeminiAgentProvider(Client client) : IAgentProvider, IAgentProviderState
{
    public JsonElement SaveNative(object native) => JsonSerializer.SerializeToElement(new { version = 1, message = (NativeMessage)native });
    public object RestoreNative(JsonElement native)
    {
        if (native.GetProperty("version").GetInt32() != 1) throw new ArgumentException("Unsupported Gemini continuation version.");
        return native.GetProperty("message").Deserialize<NativeMessage>() ?? throw new ArgumentException("Invalid Gemini continuation.");
    }
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
        AgentUsage? reported = null;
        FinishReason? finish = null; var bytes = 0;
        try
        {
            await foreach (var response in client.Models.GenerateContentStreamAsync(request.Model, contents, config, cancellationToken))
            {
                bytes = checked(bytes + JsonSerializer.SerializeToUtf8Bytes(response).Length);
                if (bytes > 16 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
                if (response.UsageMetadata is { } next)
                {
                    usage = new()
                    {
                        PromptTokenCount = next.PromptTokenCount ?? usage?.PromptTokenCount,
                        ToolUsePromptTokenCount = next.ToolUsePromptTokenCount ?? usage?.ToolUsePromptTokenCount,
                        CandidatesTokenCount = next.CandidatesTokenCount ?? usage?.CandidatesTokenCount,
                        ThoughtsTokenCount = next.ThoughtsTokenCount ?? usage?.ThoughtsTokenCount,
                        TotalTokenCount = next.TotalTokenCount ?? usage?.TotalTokenCount
                    };
                    reported = ReportedUsage(usage);
                }
                if (response.PromptFeedback?.BlockReason is { } blocked && blocked.Value != "BLOCK_REASON_UNSPECIFIED")
                    throw new AgentProviderException("safety_rejected", false, canResume: false);
                if (response.Candidates is not { Count: > 0 } candidates)
                {
                    // Without AgentHttpHandler, this SDK can erase an error-only SSE
                    // frame while converting it. Reject empty frames before any tools.
                    if (response.UsageMetadata == null && response.PromptFeedback == null) throw ProtocolError();
                    continue;
                }
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
                if (candidate.FinishReason is { } reason && reason != FinishReason.FinishReasonUnspecified) finish = reason;
            }
            if (finish == null) throw new AgentProviderException("stream_ended_without_terminal_event", true);
            if (finish != FinishReason.Stop && finish != FinishReason.MaxTokens)
                throw new AgentProviderException(finish.Value.Value switch
                {
                    "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL" => "invalid_tool_arguments",
                    "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT" or "IMAGE_RECITATION" => "safety_rejected",
                    "TOO_MANY_TOOL_CALLS" => "tool_call_limit",
                    _ => "generation_rejected"
                }, false, canResume: false);
            var calls = new List<AgentToolCall>(); var nativeCalls = new Dictionary<string, FunctionCall>(StringComparer.Ordinal);
            if (finish != FinishReason.MaxTokens)
                foreach (var part in parts)
                {
                    if (part.FunctionCall is not { } call) continue;
                    if (calls.Count >= 1024 || string.IsNullOrWhiteSpace(call.Name) || call.PartialArgs?.Count > 0 || call.WillContinue == true) throw ProtocolError();
                    var id = call.Id ?? "call_" + Guid.NewGuid().ToString("N");
                    if (string.IsNullOrWhiteSpace(id) || !nativeCalls.TryAdd(id, call)) throw ProtocolError();
                    calls.Add(new(id, call.Name, JsonSerializer.SerializeToElement(call.Args ?? [])));
                }
            var nativeContent = new Content { Role = "model", Parts = parts };
            var nativeBytes = JsonSerializer.SerializeToUtf8Bytes(nativeContent).Length;
            if (nativeBytes > 8 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
            var usageResult = reported ?? new((GetContextBytes(request) + 3L) / 4, (nativeBytes + 3L) / 4, true);
            return new(text.ToString(), calls, usageResult, new NativeMessage(nativeContent, nativeCalls), finish == FinishReason.MaxTokens);
        }
        catch (AgentProviderException error) { throw AgentProviderErrors.WithUsage(error, reported); }
        catch (ApiException error) { throw AgentProviderErrors.WithUsage(HttpError(error), reported); }
        catch (HttpRequestException) { throw AgentProviderErrors.WithUsage(new("connection_error", true), reported); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException)
        { throw AgentProviderErrors.WithUsage(ProtocolError(), reported); }
    }

    private static AgentUsage? ReportedUsage(GenerateContentResponseUsageMetadata usage)
    {
        if (usage.PromptTokenCount < 0 || usage.ToolUsePromptTokenCount < 0 || usage.CandidatesTokenCount < 0 || usage.ThoughtsTokenCount < 0 || usage.TotalTokenCount < 0)
            throw ProtocolError();
        if (usage.PromptTokenCount is not { } input || !usage.TotalTokenCount.HasValue && !usage.CandidatesTokenCount.HasValue) return null;
        var inputCount = checked((long)input + (usage.ToolUsePromptTokenCount ?? 0));
        var outputCount = usage.TotalTokenCount is { } total ? total - inputCount : checked((long)usage.CandidatesTokenCount!.Value + (usage.ThoughtsTokenCount ?? 0));
        if (outputCount < 0) throw ProtocolError();
        return new(inputCount, outputCount);
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
                var hasMedia = XamlG.Automation.AutomationMedia.TryRead(value.RootElement, out var media);
                var result = hasMedia ? media.Metadata : value.RootElement;
                var response = result.ValueKind == JsonValueKind.Object ? result.EnumerateObject().ToDictionary(item => item.Name, item => (object)item.Value.Clone()) :
                    new Dictionary<string, object> { ["output"] = result.Clone() };
                results.Add(new() { FunctionResponse = new() { Name = call.Name, Id = call.Id, Response = response } });
                if (hasMedia) foreach (var image in media.Images)
                    results.Add(new() { InlineData = new() { MimeType = image.MimeType, Data = Convert.FromBase64String(image.Data) } });
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
    private static AgentProviderException HttpError(ApiException error) => AgentProviderErrors.FromCode(error.Status, error.StatusCode, error.Message);
    private sealed record NativeMessage(Content Content, IReadOnlyDictionary<string, FunctionCall> Calls);
}
