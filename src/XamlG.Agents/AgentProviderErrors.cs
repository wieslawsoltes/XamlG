using System.Text;
using System.Text.Json;

namespace XamlG.Agents;

/// <summary>Shared provider failure policy. Only recognized protocol codes leave this
/// boundary; provider messages, arbitrary bodies and credentials are never retained.</summary>
public static class AgentProviderErrors
{
    private const int MaximumBodyBytes = 65536;

    public static AgentProviderException FromJson(string? body, int status = 0, TimeSpan? retryAfter = null)
    {
        if (body is { Length: <= MaximumBodyBytes } && Encoding.UTF8.GetByteCount(body) <= MaximumBodyBytes)
        {
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
                return FromJson(document.RootElement, status, retryAfter);
            }
            catch (JsonException) { }
        }
        return FromCode(null, status, retryAfter: retryAfter);
    }

    public static AgentProviderException FromJson(JsonElement data, int status = 0, TimeSpan? retryAfter = null)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
            data = response;
        var reason = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("incomplete_details", out var incomplete)
            ? Read(incomplete, "reason", 128) : null;
        var error = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var nested) ? nested : data;
        if (error.ValueKind == JsonValueKind.String) return FromCode(error.GetString(), status, retryAfter: retryAfter);
        return Classify(Read(error, "code", 128), Read(error, "type", 128), Read(error, "status", 128),
            Read(error, "message", 500), reason, status, retryAfter);
    }

    public static AgentProviderException FromCode(string? code, int status = 0, string? message = null, TimeSpan? retryAfter = null) =>
        Classify(code, null, null, message?.Length > 500 ? message[..500] : message, null, status, retryAfter);

    /// <summary>Classify a bounded error body and preserve Retry-After without buffering an
    /// unbounded response. The caller retains ownership of the HTTP response.</summary>
    public static async Task<AgentProviderException> FromHttpResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var delay = AgentHttpHandler.ParseRetryAfter(response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null,
            response.Headers.TryGetValues("retry-after-ms", out var milliseconds) ? milliseconds.FirstOrDefault() : null);
        var status = (int)response.StatusCode;
        if (response.Content.Headers.ContentLength > MaximumBodyBytes) return FromCode(null, status, retryAfter: delay);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaximumBodyBytes + 1]; var length = 0;
            while (length < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0) return FromJson(Encoding.UTF8.GetString(buffer, 0, length), status, delay);
                length += count;
            }
        }
        catch (Exception error) when (error is IOException or HttpRequestException) { }
        return FromCode(null, status, retryAfter: delay);
    }

    public static AgentProviderException WithUsage(AgentProviderException error, AgentUsage? usage) =>
        error.Usage != null || usage == null ? error : new(error.Code, error.Retryable, error.RetryAfter, error.CanResume)
        { Usage = usage, Details = error.Details };

    private static AgentProviderException Classify(string? code, string? type, string? providerStatus, string? message,
        string? reason, int status, TimeSpan? retryAfter)
    {
        bool Has(params string[] allowed) => allowed.Any(value => value == code || value == type || value == providerStatus || value == reason);
        // Account errors retain their exact, allowlisted codes for the account workbench.
        foreach (var account in AccountCodes)
            if (Has(account)) return new(account, false, retryAfter, canResume: true);
        if (Has("content_policy_violation", "image_content_policy_violation", "safety_violation", "refusal", "content_filter"))
            return new("safety_rejected", false, retryAfter, canResume: false);
        if (Has("context_length_exceeded", "context_window_exceeded", "prompt_too_long", "request_too_large", "model_context_window_exceeded") || IsContextMessage(message))
            return new("context_limit", false, retryAfter, canResume: true);
        if (Has("insufficient_quota", "quota_exceeded", "billing_hard_limit_reached", "credit_balance_too_low", "billing_error"))
            return new("quota_exhausted", false, retryAfter, canResume: true);
        if (status is 401 or 403 || Has("authentication_error", "invalid_api_key", "permission_error", "permission_denied", "PERMISSION_DENIED", "UNAUTHENTICATED"))
            return new("authentication_required", false, retryAfter, canResume: true);
        if (Has("invalid_request_error", "invalid_argument", "INVALID_ARGUMENT", "not_found_error", "invalid_prompt"))
            return new("request_rejected", false, retryAfter, canResume: true);
        if (Has("rate_limit_exceeded", "rate_limit_error", "RESOURCE_EXHAUSTED"))
            return new("rate_limited", true, retryAfter);
        if (Has("timeout_error", "request_timeout", "DEADLINE_EXCEEDED"))
            return new("request_timeout", true, retryAfter);
        if (Has("server_error", "internal_error", "api_error", "overloaded_error", "service_unavailable", "INTERNAL", "UNAVAILABLE"))
            return new("provider_unavailable", true, retryAfter);
        return new(status > 0 ? "http_" + status : "generation_failed", status is 408 or 429 or >= 500 and <= 599, retryAfter, canResume: true);
    }

    private static string? Read(JsonElement value, string name, int limit)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return null;
        var text = property.GetString();
        return text?.Length > limit ? text[..limit] : text;
    }

    private static bool IsContextMessage(string? message) => message != null &&
        (message.StartsWith("prompt is too long:", StringComparison.OrdinalIgnoreCase) ||
         message.StartsWith("this model's maximum context length is", StringComparison.OrdinalIgnoreCase) ||
         message.StartsWith("this model’s maximum context length is", StringComparison.OrdinalIgnoreCase) ||
         message.StartsWith("the input token count ", StringComparison.OrdinalIgnoreCase) && message.Contains("exceeds the maximum", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] AccountCodes =
    [
        "subscription_sharing_usage_limit_exceeded", "subscription_sharing_user_not_eligible", "subscription_sharing_unsupported_capability",
        "subscription_sharing_route_not_supported", "subscription_sharing_invalid_user", "chatpass_v2_scope_not_authorized",
        "chatpass_v2_invalid_authorization_context", "chatgpt_consent_required", "chatgpt_sign_in_required", "chatgpt_session_expired",
        "chatgpt_invalid_client", "chatgpt_relay_disabled"
    ];
}
