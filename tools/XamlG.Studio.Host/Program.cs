using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using XamlG.Automation;
using XamlG.Mcp;
using XamlG.Agents;
using XamlG.Agents.OpenAI;
using XamlG.Agents.Anthropic;
using XamlG.Agents.Gemini;
using XamlG.Studio.Host;
using OpenAI.Models;
using OpenAI.Responses;
using System.ClientModel.Primitives;
using System.Threading.Channels;

if (args.Any(argument => argument is "--help" or "-h"))
{
    Console.WriteLine("XamlG Studio companion\nUsage: xamlg-studio [--port=4893] [--stdio=true] [--web-root=PATH] [--origins=ORIGIN,...] [--chatgpt=true] [--chatgpt-store=PATH]\nPairs one browser IDE with authenticated MCP clients. Set distinct XAMLG_STUDIO_OWNER_TOKEN (browser) and XAMLG_STUDIO_TOKEN (MCP client), or use the generated tokens printed to stderr. Provider keys and ChatGPT account credentials remain in the companion. Account credentials persist only after an explicit remember choice.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("port", 4893);
if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = AutomationSchema.MaximumArgumentBytes);
var ownerToken = LocalToken("XAMLG_STUDIO_OWNER_TOKEN");
var clientToken = LocalToken("XAMLG_STUDIO_TOKEN");
if (EqualToken(ownerToken, clientToken)) throw new InvalidOperationException("Owner and MCP client tokens must be distinct.");
var origins = (builder.Configuration["origins"] ?? $"https://wieslawsoltes.github.io,http://127.0.0.1:{port},http://127.0.0.1:8765,http://localhost:8765")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
foreach (var origin in origins)
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != origin || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
        throw new InvalidOperationException("Origins must be exact HTTP(S) origins without paths or credentials.");
var bridge = new BrowserAutomationBridge();
var providers = new List<IAgentProvider>();
using var providerHttp = new HttpClient(new AgentHttpHandler(new SocketsHttpHandler { AllowAutoRedirect = false }))
{ Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024 };
if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") is { Length: > 0 } openAiKey)
{
    var responseOptions = new ResponsesClientOptions { RetryPolicy = new ClientRetryPolicy(0), Transport = new HttpClientPipelineTransport(providerHttp) };
    var modelOptions = new OpenAI.OpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(0), Transport = new HttpClientPipelineTransport(providerHttp) };
    if (Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") is { Length: > 0 } endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidOperationException("OPENAI_ENDPOINT must use HTTPS, or HTTP on loopback, without credentials or a query.");
        responseOptions.Endpoint = uri; modelOptions.Endpoint = uri;
    }
    providers.Add(new OpenAIAgentProvider(new ResponsesClient(new System.ClientModel.ApiKeyCredential(openAiKey), responseOptions), new OpenAIModelClient(new System.ClientModel.ApiKeyCredential(openAiKey), modelOptions)));
}
using var anthropicClient = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } anthropicKey
    ? new Anthropic.AnthropicClient { ApiKey = anthropicKey, MaxRetries = 0, HttpClient = providerHttp,
        BaseUrl = ProviderEndpoint("ANTHROPIC_ENDPOINT", "https://api.anthropic.com").AbsoluteUri.TrimEnd('/') } : null;
if (anthropicClient != null) providers.Add(new AnthropicAgentProvider(anthropicClient));
using var geminiClient = (Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")) is { Length: > 0 } geminiKey
    ? new Google.GenAI.Client(enterprise: false, apiKey: geminiKey,
        httpOptions: new() { BaseUrl = ProviderEndpoint("GEMINI_ENDPOINT", "https://generativelanguage.googleapis.com").AbsoluteUri.TrimEnd('/'), RetryOptions = new() { Attempts = 1 } },
        clientOptions: new() { HttpClientFactory = () => providerHttp }) : null;
if (geminiClient != null) providers.Add(new GeminiAgentProvider(geminiClient));
using var mcpTasks = new AutomationMcpTaskStore();
ChatGptAccountManager? chatGpt = null; string? chatGptError = null;
if (builder.Configuration.GetValue("chatgpt", true))
{
    try
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (builder.Configuration["chatgpt-store"] == null && string.IsNullOrEmpty(appData)) throw new ChatGptAccountException("local_data_directory_unavailable");
        var directory = builder.Configuration["chatgpt-store"] ?? Path.Combine(appData, "XamlG", "Studio", "ChatGPT");
        var accountOptions = new ChatGptAccountOptions();
        if (builder.Configuration["chatgpt-auth-origin"] != null || builder.Configuration["chatgpt-api-endpoint"] != null)
        {
            if (builder.Configuration["chatgpt-store"] == null ||
                !Uri.TryCreate(builder.Configuration["chatgpt-auth-origin"], UriKind.Absolute, out var authFixture) ||
                !Uri.TryCreate(builder.Configuration["chatgpt-api-endpoint"], UriKind.Absolute, out var apiFixture) ||
                authFixture.Scheme != "http" || authFixture.Host != "127.0.0.1" || apiFixture.Scheme != "http" || apiFixture.Host != "127.0.0.1" ||
                authFixture.GetLeftPart(UriPartial.Authority) != apiFixture.GetLeftPart(UriPartial.Authority))
                throw new ChatGptAccountException("account_fixtures_require_matching_loopback_endpoints_and_explicit_store");
            accountOptions = accountOptions with { AuthenticationOrigin = authFixture, ApiEndpoint = apiFixture };
        }
        chatGpt = await ChatGptAccountManager.CreateAsync(new ChatGptFileCredentialStore(directory), accountOptions);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ChatGptAccountException or ArgumentException)
    { chatGptError = "Account mode is unavailable. Check endpoint/store configuration, owner-only permissions, or close another companion using this store."; }
}
await using var chatGptLifetime = chatGpt;
using var agents = new AgentWorkbench(bridge, providers, new BrowserAgentWorkspace(bridge), mcpTasks, chatGpt, chatGptError);
var mcp = builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "XamlG Studio", Version = "0.1.0" })
    .WithAutomation(bridge).WithAutomationTasks(mcpTasks, () => bridge.CurrentSessionLifetime);
if (builder.Configuration.GetValue("stdio", false))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    mcp.WithStdioServerTransport();
}
else mcp.WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);
var app = builder.Build();
app.Use(async (context, next) =>
{
    // Do not trust arbitrary Host or Origin headers merely because the listener is loopback.
    var host = context.Request.Host.Host;
    if (host != "localhost" && (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address)))
    { context.Response.StatusCode = 403; return; }
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length != 0 && !origins.Contains(origin)) { context.Response.StatusCode = 403; return; }
    if (context.Request.Path.StartsWithSegments("/agent") && origin.Length != 0)
    {
        context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.Vary = "Origin";
        context.Response.Headers.AccessControlAllowHeaders = "Authorization,Content-Type,X-Xamlg-Owner-Session";
        context.Response.Headers.AccessControlAllowMethods = "GET,POST,OPTIONS";
        if (HttpMethods.IsOptions(context.Request.Method)) { context.Response.StatusCode = 204; return; }
    }
    if (context.Request.Path.StartsWithSegments("/mcp") || context.Request.Path.StartsWithSegments("/agent"))
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        var isOwner = context.Request.Path.StartsWithSegments("/agent");
        if (!EqualToken(supplied.StartsWith("Bearer ", StringComparison.Ordinal) ? supplied[7..] : "", isOwner ? ownerToken : clientToken))
        { context.Response.StatusCode = 401; return; }
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, isOwner ? "studio-owner" : "mcp-client")], "LocalToken"));
        if (isOwner)
        {
            if (!bridge.TryGetOwnerSession(context.Request.Headers["X-Xamlg-Owner-Session"].ToString(), out var ownerSession))
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "Pair the browser before using the workbench. The owner session has ended." }); return; }
            context.Items["OwnerSession"] = ownerSession;
            var aborted = context.RequestAborted;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(aborted, ownerSession);
            context.RequestAborted = lifetime.Token;
            try { await next(context); }
            finally { context.RequestAborted = aborted; }
            return;
        }
    }
    await next(context);
});
app.UseWebSockets();
app.MapGet("/health", () => new { service = "xamlg-studio", connected = bridge.IsConnected });
app.MapPost("/agent/{action}", async (string action, HttpContext context) =>
{
    try
    {
        using var body = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 64 }, context.RequestAborted);
        return Results.Json(await agents.ExecuteAsync(action, body.RootElement, context.RequestAborted, (CancellationToken)context.Items["OwnerSession"]!), AutomationJson.Options);
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or AutomationException or AgentProviderException or ChatGptAccountException)
    { return Results.Json(new { error = error.Message }, AutomationJson.Options, statusCode: 400); }
    catch (HttpRequestException)
    { return Results.Json(new { error = "The provider connection failed. Credentials were retained; retry after checking the connection." }, statusCode: 502); }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    { return Results.Json(new { error = "The provider request timed out. Retry when the connection is available." }, statusCode: 504); }
});
app.MapGet("/agent/events", async (HttpContext context) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-store";
    var events = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    void OnEvent(AgentEvent item) => events.Writer.TryWrite(item);
    agents.Harness.EventPublished += OnEvent;
    try
    {
        await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        await foreach (var item in events.Reader.ReadAllAsync(context.RequestAborted))
        {
            await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(item, AutomationJson.Options) + "\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    finally { agents.Harness.EventPublished -= OnEvent; }
});
app.Map("/bridge", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest || !origins.Contains(context.Request.Headers.Origin.ToString()))
    { context.Response.StatusCode = 403; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        using var pairing = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        pairing.CancelAfter(TimeSpan.FromSeconds(10));
        var hello = await BrowserAutomationBridge.ReceiveAsync(socket, pairing.Token);
        if (hello == null || hello.Value.GetProperty("kind").GetString() != "hello" ||
            !EqualToken(hello.Value.GetProperty("token").GetString() ?? "", ownerToken))
        { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Pairing rejected", context.RequestAborted); return; }
        var catalog = hello.Value.GetProperty("catalog").Deserialize<BrowserCatalog>(AutomationJson.Options)
            ?? throw new AutomationException("invalid_catalog", "Missing browser catalog.");
        await bridge.RunAsync(socket, catalog, context.RequestAborted);
    }
    catch (Exception error) when (error is AutomationException or JsonException or KeyNotFoundException or WebSocketException or OperationCanceledException)
    {
        if (socket.State == WebSocketState.Open)
        {
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session ended", closing.Token); }
            catch (Exception closeError) when (closeError is WebSocketException or OperationCanceledException) { }
        }
    }
});
if (!builder.Configuration.GetValue("stdio", false)) app.MapMcp("/mcp");
var webRoot = builder.Configuration["web-root"];
if (webRoot != null)
{
    var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ServeUnknownFileTypes = true, DefaultContentType = "application/octet-stream" });
}
Console.Error.WriteLine($"XamlG companion: http://127.0.0.1:{port}\nOpen https://wieslawsoltes.github.io/XamlG/ and pair ws://127.0.0.1:{port}/bridge in Agent access.\nOwner token (browser only): {ownerToken}\nMCP client token: {clientToken}");
await app.RunAsync();

static bool EqualToken(string supplied, string expected) =>
    CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

static string LocalToken(string variable)
{
    var value = Environment.GetEnvironmentVariable(variable) ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    if (value.Length is < 32 or > 256 || value.Any(char.IsWhiteSpace))
        throw new InvalidOperationException(variable + " must contain 32–256 non-whitespace characters.");
    return value;
}

static Uri ProviderEndpoint(string variable, string defaultEndpoint)
{
    var endpoint = Environment.GetEnvironmentVariable(variable) ?? defaultEndpoint;
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
        (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
        throw new InvalidOperationException(variable + " must use HTTPS, or HTTP on loopback, without credentials or a query.");
    return uri;
}
