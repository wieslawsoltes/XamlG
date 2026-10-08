using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using XamlG.Agents;

namespace XamlG.Studio.Host;

/// <summary>Owner-authenticated provider transport. No project/IDE access or MCP session is required.</summary>
internal sealed partial class ProviderRelay(HttpClient http, IReadOnlyDictionary<string, ProviderRelay.Connection> connections)
{
    private readonly SemaphoreSlim _requests = new(4, 4);
    internal sealed record Connection(string Key, Uri Endpoint);
    public async Task SendAsync(string provider, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!connections.TryGetValue(provider, out var connection))
        { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = new { code = "provider_not_configured" } }); return; }
        var target = context.Request.Query["target"].ToString();
        if (!ValidTarget(provider, context.Request.Method, target))
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "invalid_provider_operation" } }); return; }
        if (!await _requests.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "1";
            await context.Response.WriteAsJsonAsync(new { error = new { code = "provider_busy" } }); return;
        }
        try { await ForwardAsync(provider, connection, target, context); }
        finally { _requests.Release(); }
    }
    private async Task ForwardAsync(string provider, Connection connection, string target, HttpContext context)
    {
        var prefix = connection.Endpoint.AbsolutePath.TrimEnd('/');
        // Official clients include their API version in the relative operation path.
        var version = provider == "gemini" ? "/v1beta" : "/v1";
        if (prefix.EndsWith(version, StringComparison.Ordinal) && target.StartsWith(version + "/", StringComparison.Ordinal)) target = target[version.Length..];
        var upstream = new Uri(connection.Endpoint.GetLeftPart(UriPartial.Authority) + prefix + target);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstream);
        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (context.Request.ContentType?.Split(';')[0].Trim() != "application/json")
            { context.Response.StatusCode = 415; return; }
            request.Content = new StreamContent(context.Request.Body);
            request.Content.Headers.ContentType = new("application/json");
        }
        switch (provider)
        {
            case "openai": request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Key); break;
            case "anthropic": request.Headers.TryAddWithoutValidation("x-api-key", connection.Key); request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01"); break;
            case "gemini": request.Headers.TryAddWithoutValidation("x-goog-api-key", connection.Key); break;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[16384]; long total = 0;
            while (true)
            {
                var count = await stream.ReadAsync(buffer, deadline.Token); if (count == 0) break;
                total += count;
                if (total > 32 * 1024 * 1024) throw new AgentProviderException("response_too_large", false);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                await context.Response.Body.FlushAsync(deadline.Token);
            }
        }
        catch (Exception error) when (error is AgentProviderException or HttpRequestException or OperationCanceledException)
        {
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
            var failure = error as AgentProviderException;
            context.Response.StatusCode = failure is { Details.HttpStatus: >= 400 and <= 599 } known ? known.Details.HttpStatus : error is OperationCanceledException ? 504 : 502;
            if (failure?.RetryAfter is { } delay) context.Response.Headers.RetryAfter = Math.Ceiling(Math.Min(delay.TotalSeconds, 86400)).ToString(CultureInfo.InvariantCulture);
            await context.Response.WriteAsJsonAsync(new { error = new { code = failure?.Code ?? "provider_connection_failed" } }, context.RequestAborted);
        }
    }
    internal static bool ValidTarget(string provider, string method, string target)
    {
        if (target.Length is < 1 or > 4096 || target.Contains('#') || target.Contains('\\') || !target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal)) return false;
        var pieces = target.Split('?', 2); var path = pieces[0];
        var validPath = method == "GET" ? provider switch
        {
            "openai" or "anthropic" => path == "/v1/models",
            "gemini" => path is "/v1beta/models" or "/v1/models", _ => false
        } : method == "POST" && (provider switch
        {
            "openai" => path == "/v1/responses", "anthropic" => path == "/v1/messages",
            "gemini" => GeminiOperation().IsMatch(path), _ => false
        });
        if (!validPath) return false;
        if (pieces.Length == 1) return true;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in pieces[1].Split('&'))
        {
            var pair = item.Split('=', 2); var key = Uri.UnescapeDataString(pair[0]);
            if (!keys.Add(key) || pair.Length != 2 || pair[1].Length > 2048 || key is not ("limit" or "after_id" or "before_id" or "page_size" or "page_token" or "pageSize" or "pageToken" or "alt")) return false;
            if (key == "alt" && pair[1] != "sse") return false;
        }
        return true;
    }
    [GeneratedRegex(@"^/v1(?:beta)?/models/[a-zA-Z0-9._-]+:(?:streamGenerateContent|generateContent)$", RegexOptions.CultureInvariant)]
    private static partial Regex GeminiOperation();
}
