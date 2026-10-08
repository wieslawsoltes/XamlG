using System.Globalization;

namespace XamlG.Agents;

/// <summary>Optional transport for official SDK clients. Carries only canonical status
/// failure codes and Retry-After advice into the harness, without provider bodies or request IDs.
/// SDK retries must be disabled; the harness owns the request budget and cooldown.</summary>
public sealed class AgentHttpHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            if (response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
                response.Content = new AgentEventStreamContent(response.Content);
            return response;
        }
        using (response)
        {
            throw await AgentProviderErrors.FromHttpResponseAsync(response, cancellationToken);
        }
    }

    public static TimeSpan? ParseRetryAfter(string? value, string? milliseconds = null)
    {
        if (double.TryParse(milliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && double.IsFinite(ms) && ms >= 0)
            return ms >= TimeSpan.MaxValue.TotalMilliseconds ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(ms);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds >= 0)
            return seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        return null;
    }
}
