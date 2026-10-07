using System.ClientModel;
using System.ClientModel.Primitives;
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
        catch (ClientResultException error) { throw new AgentProviderException("http_" + error.Status, error.Status is 408 or 429 or >= 500); }
        catch (HttpRequestException) { throw new AgentProviderException("connection_error", true); }
        catch (JsonException) { throw new AgentProviderException("invalid_response_json", false); }
    }

    public int GetContextBytes(AgentRequest request) => ModelReaderWriter.Write(Options(request, chatGptPlan)).ToMemory().Length;

    public async Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> textDelta, CancellationToken cancellationToken)
    {
        var options = Options(request, chatGptPlan);
        ResponseResult? terminal = null; var incomplete = false; var publicCharacters = 0;
        try
        {
            await foreach (var update in responses.CreateResponseStreamingAsync(options, cancellationToken))
            {
                switch (update)
                {
                    case StreamingResponseOutputTextDeltaUpdate text:
                        publicCharacters = checked(publicCharacters + text.Delta.Length);
                        if (publicCharacters > 4_000_000) throw new AgentProviderException("response_too_large", false);
                        await textDelta(text.Delta); break;
                    case StreamingResponseCompletedUpdate completed:
                        if (terminal != null) throw new AgentProviderException("duplicate_terminal_event", false);
                        terminal = completed.Response; break;
                    case StreamingResponseIncompleteUpdate limited:
                        if (terminal != null) throw new AgentProviderException("duplicate_terminal_event", false);
                        terminal = limited.Response; incomplete = true; break;
                    case StreamingResponseFailedUpdate:
                        throw new AgentProviderException("generation_failed", false);
                }
            }
        }
        catch (ClientResultException error)
        {
            var retryable = error.Status is 408 or 429 or >= 500;
            throw new AgentProviderException("http_" + error.Status, retryable);
        }
        catch (HttpRequestException) { throw new AgentProviderException("connection_error", true); }
        catch (JsonException) { throw new AgentProviderException("invalid_response_json", false); }
        if (terminal == null) throw new AgentProviderException("stream_ended_without_terminal_event", true);
        if (ModelReaderWriter.Write(terminal).ToMemory().Length > 8 * 1024 * 1024)
            throw new AgentProviderException("response_too_large", false);
        var calls = new List<AgentToolCall>();
        foreach (var call in terminal.OutputItems.OfType<FunctionCallResponseItem>())
        {
            try
            {
                if (chatGptPlan)
                {
                    // The pinned SDK preserves unmodeled namespace fields in native items.
                    // Reject foreign namespaces before mapping a call to an IDE capability.
                    using var wire = JsonDocument.Parse(ModelReaderWriter.Write(call).ToMemory());
                    if (!wire.RootElement.TryGetProperty("namespace", out var ns) || ns.ValueKind != JsonValueKind.String || ns.GetString() != "xamlg")
                        throw new AgentProviderException("invalid_tool_namespace", false, canResume: false);
                }
                using var arguments = JsonDocument.Parse(call.FunctionArguments.ToMemory(), new JsonDocumentOptions { MaxDepth = 64 });
                calls.Add(new(call.CallId, call.FunctionName, arguments.RootElement.Clone()));
            }
            catch (JsonException) { throw new AgentProviderException("invalid_tool_arguments", false); }
        }
        var usage = terminal.Usage == null
            ? new AgentUsage((GetContextBytes(request) + 3L) / 4, (publicCharacters + 3L) / 4, Estimated: true)
            : new AgentUsage(terminal.Usage.InputTokenCount, terminal.Usage.OutputTokenCount);
        return new(terminal.GetOutputText() ?? string.Empty, calls, usage, terminal.OutputItems.ToArray(), incomplete);
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
