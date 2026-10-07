using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
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
    Console.WriteLine("XamlG Studio companion\nUsage: xamlg-studio [--port=4893] [--stdio=true] [--web-root=PATH] [--origins=ORIGIN,...]\nPairs the browser IDE with authenticated MCP and agent clients. Set XAMLG_STUDIO_TOKEN or use the generated local token printed to stderr. Provider credentials remain in the host environment.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("port", 4893);
if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = AutomationSchema.MaximumArgumentBytes);
var token = Environment.GetEnvironmentVariable("XAMLG_STUDIO_TOKEN") ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
if (token.Length < 32) throw new InvalidOperationException("XAMLG_STUDIO_TOKEN must contain at least 32 characters.");
var origins = (builder.Configuration["origins"] ?? $"http://127.0.0.1:{port},http://127.0.0.1:8765,http://localhost:8765")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
foreach (var origin in origins)
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != origin || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
        throw new InvalidOperationException("Origins must be exact HTTP(S) origins without paths or credentials.");
var bridge = new BrowserAutomationBridge();
var providers = new List<IAgentProvider>();
using var providerHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
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
using var agents = new AgentWorkbench(bridge, providers, new BrowserAgentWorkspace(bridge));
bridge.CatalogChanged += () => { if (!bridge.IsConnected) agents.Stop(); };
var mcp = builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "XamlG Studio", Version = "0.1.0" })
    .WithAutomation(bridge);
if (builder.Configuration.GetValue("stdio", false))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    mcp.WithStdioServerTransport();
}
else mcp.WithHttpTransport();
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
        context.Response.Headers.AccessControlAllowHeaders = "Authorization,Content-Type";
        context.Response.Headers.AccessControlAllowMethods = "GET,POST,OPTIONS";
        if (HttpMethods.IsOptions(context.Request.Method)) { context.Response.StatusCode = 204; return; }
    }
    if (context.Request.Path.StartsWithSegments("/mcp") || context.Request.Path.StartsWithSegments("/agent"))
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        if (!EqualToken(supplied.StartsWith("Bearer ", StringComparison.Ordinal) ? supplied[7..] : "", token))
        { context.Response.StatusCode = 401; return; }
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
        return Results.Json(await agents.ExecuteAsync(action, body.RootElement, context.RequestAborted), AutomationJson.Options);
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or AutomationException or AgentProviderException)
    { return Results.Json(new { error = error.Message }, AutomationJson.Options, statusCode: 400); }
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
            !EqualToken(hello.Value.GetProperty("token").GetString() ?? "", token))
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
Console.Error.WriteLine($"XamlG companion: http://127.0.0.1:{port}\nLocal access token: {token}");
await app.RunAsync();

static bool EqualToken(string supplied, string expected) =>
    CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

static Uri ProviderEndpoint(string variable, string defaultEndpoint)
{
    var endpoint = Environment.GetEnvironmentVariable(variable) ?? defaultEndpoint;
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
        (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
        throw new InvalidOperationException(variable + " must use HTTPS, or HTTP on loopback, without credentials or a query.");
    return uri;
}
