using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace XamlG.Agents.Anthropic;

/// <summary>Messages API transport through the official Anthropic SDK. Native signed content
/// stays in memory, separate from public text. Configure the injected client with MaxRetries=0
/// so that the harness accounts for every request and owns retry/cancellation policy.</summary>
public sealed class AnthropicAgentProvider(AnthropicClient client) : IAgentProvider
{
    public string Id => "anthropic";

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = new List<string>();
            var page = await client.Models.WithOptions(options => options with { MaxRetries = 0 }).List(cancellationToken: cancellationToken);
            for (var index = 0; ; index++)
            {
                result.AddRange(page.Items.Select(model => model.ID));
                if (result.Count > 10000 || index >= 100) throw new AgentProviderException("model_catalog_too_large", false);
                if (!page.HasNext()) break;
                page = await page.Next(cancellationToken);
            }
            return result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (AnthropicApiException error) { throw HttpError(error); }
        catch (Exception error) when (error is AnthropicIOException or HttpRequestException) { throw new AgentProviderException("connection_error", true); }
        catch (Exception error) when (error is AnthropicInvalidDataException or JsonException) { throw ProtocolError(); }
    }

    public int GetContextBytes(AgentRequest request) => JsonSerializer.SerializeToUtf8Bytes(Options(request).RawBodyData).Length;

    public async Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> textDelta, CancellationToken cancellationToken)
    {
        var blocks = new List<Block>();
        var started = false; var stopped = false; string? stopReason = null;
        long? inputTokens = null, outputTokens = null;
        var streamBytes = 0;
        try
        {
            await foreach (var update in client.Messages.WithOptions(options => options with { MaxRetries = 0 }).CreateStreaming(Options(request), cancellationToken))
            {
                var data = update.Json;
                streamBytes = checked(streamBytes + Encoding.UTF8.GetByteCount(data.GetRawText()));
                if (streamBytes > 16 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
                if (stopped) throw ProtocolError();
                var type = data.GetProperty("type").GetString();
                if (type == "message_start")
                {
                    if (started) throw ProtocolError();
                    started = true;
                    var message = data.GetProperty("message");
                    if (message.GetProperty("role").GetString() != "assistant" || message.GetProperty("content").GetArrayLength() != 0) throw ProtocolError();
                    if (message.TryGetProperty("usage", out var usage))
                    {
                        inputTokens = Count(usage, "input_tokens") + (Count(usage, "cache_creation_input_tokens") ?? 0) + (Count(usage, "cache_read_input_tokens") ?? 0);
                        outputTokens = Count(usage, "output_tokens");
                    }
                    continue;
                }
                if (!started) throw ProtocolError();
                switch (type)
                {
                    case "content_block_start":
                        if (blocks.Count >= 10000 || data.GetProperty("index").GetInt32() != blocks.Count) throw ProtocolError();
                        var block = new Block(JsonNode.Parse(data.GetProperty("content_block").GetRawText())!.AsObject());
                        blocks.Add(block);
                        if (block.Type == "text" && block.Value["text"]?.GetValue<string>() is { Length: > 0 } initialText) await textDelta(initialText);
                        break;
                    case "content_block_delta":
                        var current = OpenBlock(blocks, data);
                        var delta = data.GetProperty("delta");
                        switch (delta.GetProperty("type").GetString())
                        {
                            case "text_delta" when current.Type == "text":
                                var text = delta.GetProperty("text").GetString()!;
                                current.Append("text", text); await textDelta(text); break;
                            case "thinking_delta" when current.Type == "thinking":
                                current.Append("thinking", delta.GetProperty("thinking").GetString()!); break;
                            case "signature_delta" when current.Type == "thinking":
                                current.Append("signature", delta.GetProperty("signature").GetString()!); break;
                            case "input_json_delta" when current.Type == "tool_use":
                                current.Input.Append(delta.GetProperty("partial_json").GetString()); break;
                            case "citations_delta" when current.Type == "text":
                                var citations = current.Value["citations"] as JsonArray ?? new JsonArray();
                                if (current.Value["citations"] == null) current.Value["citations"] = citations;
                                citations.Add(JsonNode.Parse(delta.GetProperty("citation").GetRawText())); break;
                            default: throw new AgentProviderException("unsupported_stream_delta", false, canResume: false);
                        }
                        break;
                    case "content_block_stop":
                        OpenBlock(blocks, data).Close(); break;
                    case "message_delta":
                        if (data.GetProperty("delta").TryGetProperty("stop_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                        {
                            if (stopReason != null) throw ProtocolError();
                            stopReason = reason.GetString();
                        }
                        if (data.TryGetProperty("usage", out var nextUsage)) outputTokens = Count(nextUsage, "output_tokens") ?? outputTokens;
                        break;
                    case "message_stop":
                        if (blocks.Any(item => !item.Closed) || stopReason == null) throw ProtocolError();
                        stopped = true; break;
                    default: throw ProtocolError();
                }
            }
        }
        catch (AnthropicApiException error) { throw HttpError(error); }
        catch (AnthropicIOException) { throw new AgentProviderException("connection_error", true); }
        catch (HttpRequestException) { throw new AgentProviderException("connection_error", true); }
        catch (AnthropicSseException) { throw new AgentProviderException("provider_stream_error", false); }
        catch (Exception error) when (error is JsonException or AnthropicInvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw ProtocolError(); }
        if (!stopped) throw new AgentProviderException("stream_ended_without_terminal_event", true);
        if (stopReason == "refusal") throw new AgentProviderException("safety_rejected", false, canResume: false);
        if (stopReason == "model_context_window_exceeded") throw new AgentProviderException("context_limit", false);
        if (stopReason is not ("end_turn" or "stop_sequence" or "tool_use" or "max_tokens")) throw new AgentProviderException("unsupported_stop_reason", false, canResume: false);
        var calls = new List<AgentToolCall>();
        if (stopReason != "max_tokens")
        {
            try
            {
                foreach (var block in blocks.Where(block => block.Type == "tool_use"))
                {
                    if (block.Input.Length != 0) block.Value["input"] = JsonNode.Parse(block.Input.ToString());
                    var id = block.Value["id"]?.GetValue<string>(); var name = block.Value["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) throw ProtocolError();
                    calls.Add(new(id, name, JsonSerializer.SerializeToElement(block.Value["input"])));
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { throw ProtocolError(); }
        }
        if (stopReason != "max_tokens" && ((stopReason == "tool_use") != (calls.Count > 0))) throw ProtocolError();
        var content = JsonSerializer.SerializeToElement(blocks.Select(block => block.Value).ToArray());
        if (Encoding.UTF8.GetByteCount(content.GetRawText()) > 8 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
        var native = MessageParam.FromRawUnchecked(new Dictionary<string, JsonElement>
        { ["role"] = JsonSerializer.SerializeToElement("assistant"), ["content"] = content });
        var publicText = string.Concat(blocks.Where(block => block.Type == "text").Select(block => block.Value["text"]?.GetValue<string>()));
        var usageResult = inputTokens.HasValue && outputTokens.HasValue ? new AgentUsage(inputTokens.Value, outputTokens.Value) :
            new AgentUsage((GetContextBytes(request) + 3L) / 4, (Encoding.UTF8.GetByteCount(content.GetRawText()) + 3L) / 4, true);
        return new(publicText, calls, usageResult, new NativeMessage(native), stopReason == "max_tokens");
    }

    private static MessageCreateParams Options(AgentRequest request)
    {
        var messages = new List<MessageParam>(); var results = new List<ContentBlockParam>();
        void FlushResults()
        {
            if (results.Count == 0) return;
            messages.Add(new() { Role = Role.User, Content = new MessageParamContent(results.ToArray()) }); results.Clear();
        }
        foreach (var message in request.Messages)
        {
            if (message.Kind == AgentMessageKind.ToolResult)
            {
                results.Add(new ToolResultBlockParam { ToolUseID = message.ToolCallId!, Content = message.Text });
                continue;
            }
            FlushResults();
            if (message.Kind == AgentMessageKind.User) messages.Add(new() { Role = Role.User, Content = message.Text });
            else if (message.Native is NativeMessage native) messages.Add(native.Message);
            else throw new InvalidOperationException("The task contains a different provider's native context.");
        }
        FlushResults();
        return new()
        {
            Model = request.Model, MaxTokens = request.MaxOutputTokens, System = request.Instructions, Messages = messages,
            Tools = request.Tools.Select(tool => (ToolUnion)new Tool
            {
                Name = tool.Name, Description = tool.Description,
                InputSchema = InputSchema.FromRawUnchecked(tool.InputSchema.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone()))
            }).ToArray()
        };
    }

    private static long? Count(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var value)) return null;
        var count = value.GetInt64(); return count >= 0 ? count : throw ProtocolError();
    }
    private static Block OpenBlock(List<Block> blocks, JsonElement data)
    {
        var index = data.GetProperty("index").GetInt32();
        return index >= 0 && index < blocks.Count && !blocks[index].Closed ? blocks[index] : throw ProtocolError();
    }
    private static AgentProviderException ProtocolError() => new("invalid_response_protocol", false);
    private static AgentProviderException HttpError(AnthropicApiException error) => new("http_" + (int)error.StatusCode, (int)error.StatusCode is 408 or 429 or >= 500);
    private sealed record NativeMessage(MessageParam Message);
    private sealed class Block(JsonObject value)
    {
        public JsonObject Value { get; } = value;
        public string Type { get; } = value["type"]!.GetValue<string>();
        public bool Closed { get; private set; }
        public StringBuilder Input { get; } = new();
        private readonly Dictionary<string, StringBuilder> _deltas = [];
        public void Append(string key, string text)
        {
            if (!_deltas.TryGetValue(key, out var buffer)) _deltas[key] = buffer = new(Value[key]?.GetValue<string>() ?? "");
            buffer.Append(text);
        }
        public void Close()
        {
            foreach (var (key, text) in _deltas) Value[key] = text.ToString();
            _deltas.Clear(); Closed = true;
        }
    }
}
