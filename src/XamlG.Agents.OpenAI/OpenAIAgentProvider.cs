using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using OpenAI.Models;
using OpenAI.Responses;

namespace XamlG.Agents.OpenAI;

/// <summary>
/// Native Responses continuation through the official OpenAI SDK. Reasoning items and
/// encrypted content remain intact in memory and are never included in public transcripts.
/// Construct SDK clients with server-side credentials and inject them into this adapter.
/// </summary>
public sealed class OpenAIAgentProvider(ResponsesClient responses, OpenAIModelClient? models = null, bool chatGptPlan = false) : IAgentProvider
{
    public string Id => "openai";
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        if (models == null) throw new InvalidOperationException("Supply the official model client to enable discovery.");
        try
        {
            var result = await models.GetModelsAsync(cancellationToken);
            return result.Value.Select(m => m.Id).Order(StringComparer.Ordinal).ToArray();
        }
        catch (ClientResultException error) { throw HttpError(error); }
        catch (HttpRequestException) { throw new AgentProviderException("connection_error", true); }
        catch (JsonException) { throw new AgentProviderException("invalid_response_json", false); }
    }

    public int GetContextBytes(AgentRequest request) => ModelReaderWriter.Write(Options(request, chatGptPlan)).ToMemory().Length;

    public async Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> textDelta, CancellationToken cancellationToken)
    {
        var options = Options(request, chatGptPlan);
        ResponseResult? terminal = null; ResponseStatus? expectedStatus = null;
        AgentUsage? usage = null; var publicBytes = 0; var streamBytes = 0; var refusal = false;
        async ValueTask Publish(string text)
        {
            publicBytes = checked(publicBytes + Encoding.UTF8.GetByteCount(text));
            if (publicBytes > 8 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
            await textDelta(text);
        }
        try
        {
            await foreach (var update in responses.CreateResponseStreamingAsync(options, cancellationToken))
            {
                if (terminal != null)
                    throw new AgentProviderException(update is StreamingResponseCompletedUpdate or StreamingResponseIncompleteUpdate or StreamingResponseFailedUpdate
                        ? "duplicate_terminal_event" : "invalid_response_protocol", false);
                streamBytes = checked(streamBytes + ModelReaderWriter.Write(update).ToMemory().Length);
                if (streamBytes > 16 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
                switch (update)
                {
                    case StreamingResponseOutputTextDeltaUpdate text:
                        await Publish(text.Delta); break;
                    case StreamingResponseRefusalDeltaUpdate rejected:
                        refusal = true; await Publish(rejected.Delta); break;
                    case StreamingResponseCompletedUpdate completed:
                        terminal = completed.Response; expectedStatus = ResponseStatus.Completed; break;
                    case StreamingResponseIncompleteUpdate limited:
                        terminal = limited.Response; expectedStatus = ResponseStatus.Incomplete; break;
                    case StreamingResponseFailedUpdate failed:
                        terminal = failed.Response; expectedStatus = ResponseStatus.Failed; break;
                    case StreamingResponseErrorUpdate error:
                        throw AgentProviderErrors.FromCode(error.Code, message: error.Message);
                }
                if (terminal?.Usage is { } reported)
                {
                    if (reported.InputTokenCount < 0 || reported.OutputTokenCount < 0) throw new AgentProviderException("invalid_response_protocol", false);
                    usage = new(reported.InputTokenCount, reported.OutputTokenCount);
                }
            }
            if (terminal == null) throw new AgentProviderException("stream_ended_without_terminal_event", true);
            var nativeBytes = ModelReaderWriter.Write(terminal).ToMemory().Length;
            if (nativeBytes > 8 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
            if (terminal.Error != null) throw AgentProviderErrors.FromJson(ModelReaderWriter.Write(terminal.Error).ToString());
            if (terminal.Status != expectedStatus) throw new AgentProviderException("invalid_response_protocol", false);
            if (terminal.Status == ResponseStatus.Failed) throw new AgentProviderException("generation_failed", false, canResume: true);
            var content = terminal.OutputItems.OfType<MessageResponseItem>().SelectMany(item => item.Content).ToArray();
            refusal |= content.Any(part => part.Kind == ResponseContentPartKind.Refusal);
            if (refusal)
            {
                if (publicBytes == 0) await Publish(string.Concat(content.Where(part => part.Kind == ResponseContentPartKind.Refusal).Select(part => part.Refusal)));
                throw new AgentProviderException("safety_rejected", false, canResume: false);
            }
            var incomplete = terminal.Status == ResponseStatus.Incomplete;
            if (incomplete && terminal.IncompleteStatusDetails?.Reason != ResponseIncompleteStatusReason.MaxOutputTokens)
                throw AgentProviderErrors.FromCode(terminal.IncompleteStatusDetails?.Reason?.ToString());
            var calls = new List<AgentToolCall>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            // Truncated function JSON is never parsed, committed or executed. The harness
            // accounts for this attempt and pauses with the original continuation intact.
            if (!incomplete) foreach (var call in terminal.OutputItems.OfType<FunctionCallResponseItem>())
            {
                if (calls.Count >= 1024 || string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.FunctionName) || !ids.Add(call.CallId) ||
                    call.Status is { } status && status != FunctionCallStatus.Completed)
                    throw new AgentProviderException("invalid_response_protocol", false);
                if (chatGptPlan)
                {
                    // The pinned SDK preserves unmodeled namespace fields in native items.
                    // Reject foreign namespaces before mapping a call to an IDE capability.
                    using var wire = JsonDocument.Parse(ModelReaderWriter.Write(call).ToMemory());
                    if (!wire.RootElement.TryGetProperty("namespace", out var ns) || ns.ValueKind != JsonValueKind.String || ns.GetString() != "xamlg")
                        throw new AgentProviderException("invalid_tool_namespace", false, canResume: false);
                }
                try
                {
                    using var arguments = JsonDocument.Parse(call.FunctionArguments.ToMemory(), new JsonDocumentOptions { MaxDepth = 64 });
                    if (arguments.RootElement.ValueKind != JsonValueKind.Object) throw new AgentProviderException("invalid_tool_arguments", false);
                    calls.Add(new(call.CallId, call.FunctionName, arguments.RootElement.Clone()));
                }
                catch (JsonException) { throw new AgentProviderException("invalid_tool_arguments", false); }
            }
            usage ??= new((GetContextBytes(request) + 3L) / 4, (nativeBytes + 3L) / 4, Estimated: true);
            return new(terminal.GetOutputText() ?? string.Empty, calls, usage, terminal.OutputItems.ToArray(), incomplete)
            { OutputLimitCanBeIncreased = !chatGptPlan };
        }
        catch (AgentProviderException error) { throw AgentProviderErrors.WithUsage(error, usage); }
        catch (ClientResultException error) { throw AgentProviderErrors.WithUsage(HttpError(error), usage); }
        catch (HttpRequestException) { throw AgentProviderErrors.WithUsage(new("connection_error", true), usage); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw AgentProviderErrors.WithUsage(new("invalid_response_protocol", false), usage); }
    }

    private static AgentProviderException HttpError(ClientResultException error)
    {
        var response = error.GetRawResponse();
        var delay = response == null ? null : AgentHttpHandler.ParseRetryAfter(
            response.Headers.TryGetValue("Retry-After", out var value) ? value : null,
            response.Headers.TryGetValue("retry-after-ms", out var milliseconds) ? milliseconds : null);
        try
        {
            if (response?.Content is { } content && content.ToMemory().Length <= 65536)
                return AgentProviderErrors.FromJson(content.ToString(), error.Status, delay);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException) { }
        return AgentProviderErrors.FromCode(null, error.Status, retryAfter: delay);
    }

    internal static CreateResponseOptions Options(AgentRequest request, bool chatGptPlan = false)
    {
        var items = new List<ResponseItem>();
        foreach (var message in request.Messages)
        {
            switch (message.Kind)
            {
                case AgentMessageKind.User: items.Add(ResponseItem.CreateUserMessageItem(message.Text)); break;
                case AgentMessageKind.ToolResult:
                    items.Add(new FunctionCallOutputResponseItem(message.ToolCallId!, message.Text)); break;
                case AgentMessageKind.Assistant when message.Native is ResponseItem[] native:
                    items.AddRange(native); break;
                case AgentMessageKind.Assistant:
                    throw new InvalidOperationException("The task contains a different provider's native context.");
            }
        }
        var options = new CreateResponseOptions(request.Model, items)
        {
            Instructions = request.Instructions, StoredOutputEnabled = false, StreamingEnabled = true,
            MaxOutputTokenCount = chatGptPlan ? null : request.MaxOutputTokens
        };
        options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        if (chatGptPlan && request.Tools.Count != 0)
        {
            // Namespace tools are a supported API wire shape not yet represented by a named
            // SDK class. The official SDK's persistable-model extension preserves it losslessly.
            var toolNamespace = BinaryData.FromObjectAsJson(new { type = "namespace", name = "xamlg", description = "XamlG IDE operations",
                tools = request.Tools.Select(tool => new { type = "function", name = tool.Name, description = tool.Description, parameters = tool.InputSchema, strict = false }) });
            options.Tools.Add(ModelReaderWriter.Read<ResponseTool>(toolNamespace)!);
        }
        else foreach (var tool in request.Tools)
                options.Tools.Add(ResponseTool.CreateFunctionTool(functionName: tool.Name, functionDescription: tool.Description,
                    functionParameters: BinaryData.FromString(tool.InputSchema.GetRawText()), strictModeEnabled: false));
        return options;
    }
}
