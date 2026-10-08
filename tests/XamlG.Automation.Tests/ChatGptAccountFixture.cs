using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using XamlG.Agents.OpenAI;
using Xunit;

namespace XamlG.Automation.Tests;

internal sealed class ChatGptAccountFixture : IAsyncDisposable
{
    private readonly WebApplication _server;
    private readonly List<RSA> _keys = [];
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _codes = new();
    private RSA _key = null!;
    private string _keyId = "";
    private int _registration, _issued;
    public readonly CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(40));
    public readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "xamlg-chatgpt-" + Guid.NewGuid().ToString("N"));
    public readonly ConcurrentQueue<Dictionary<string, string>> Authorizations = new(), TokenRequests = new(), Revocations = new();
    public readonly ConcurrentQueue<string> ModelAuthorizations = new();
    public readonly ConcurrentQueue<(string Authorization, JsonElement Body)> InferenceRequests = new();
    public readonly TaskCompletionSource RefreshEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? HoldRefresh;
    public string? IdentityFault, RefreshSubject;
    public string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    public int InitialExpiry = 3600, TokenFailures, KeyFailures, RevocationStatus = 200;
    public string? RefreshError;
    public bool OmitRefreshIdentity;
    public CancellationToken Token => Timeout.Token;
    public Uri Origin => new(_server.Urls.Single() + "/");
    public ChatGptAccountOptions Options => new() { AuthenticationOrigin = Origin, ApiEndpoint = new(Origin, "v1/") };
    private ChatGptAccountFixture(WebApplication server) { _server = server; RotateSigningKey(); }
    public static async Task<ChatGptAccountFixture> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var server = builder.Build(); var fixture = new ChatGptAccountFixture(server);
        server.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        { issuer = fixture.Origin.AbsoluteUri.TrimEnd('/'), jwks_uri = new Uri(fixture.Origin, "keys").AbsoluteUri, revocation_endpoint = new Uri(fixture.Origin, "revoke").AbsoluteUri }));
        server.MapGet("/keys", () =>
        {
            if (fixture.KeyFailures > 0) { fixture.KeyFailures--; return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503); }
            var key = fixture._key.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = fixture._keyId,
                n = Encode(key.Modulus!), e = Encode(key.Exponent!) } } });
        });
        server.MapGet("/api/accounts/authorize", (HttpContext context) =>
        {
            var fields = context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            fixture.Authorizations.Enqueue(fields);
            var client = fields["client_id"] == "dynamic_agent_client" ? "oaiapp_fixture_" + Interlocked.Increment(ref fixture._registration) : fields["client_id"];
            var code = Guid.NewGuid().ToString("N"); fields["issued_client"] = client; fixture._codes[code] = fields;
            return Results.Redirect(QueryHelpers.AddQueryString(fields["redirect_uri"], new Dictionary<string, string?>
            { ["state"] = fields["state"], ["code"] = code, ["client_id"] = client }));
        });
        server.MapPost("/api/accounts/oauth/token", async (HttpContext context) =>
        {
            var fields = (await context.Request.ReadFormAsync(context.RequestAborted)).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            fixture.TokenRequests.Enqueue(fields);
            if (fixture.TokenFailures > 0) { fixture.TokenFailures--; return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503); }
            var refresh = fields["grant_type"] == "refresh_token";
            string? nonce = null;
            if (refresh)
            {
                fixture.RefreshEntered.TrySetResult();
                if (fixture.HoldRefresh != null) await fixture.HoldRefresh.Task.WaitAsync(context.RequestAborted);
                if (fixture.RefreshError != null) return Results.Json(new { error = fixture.RefreshError }, statusCode: 400);
            }
            else
            {
                if (!fixture._codes.TryRemove(fields["code"], out var authorization) || fields["client_id"] != authorization["issued_client"] ||
                    fields["redirect_uri"] != authorization["redirect_uri"] || Encode(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"]))) != authorization["code_challenge"])
                    return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                nonce = authorization["nonce"];
            }
            var number = Interlocked.Increment(ref fixture._issued);
            var body = new Dictionary<string, object?>
            {
                ["token_type"] = "Bearer", ["access_token"] = "synthetic_access_" + number, ["refresh_token"] = "synthetic_refresh_" + number,
                ["expires_in"] = refresh ? 3600 : fixture.InitialExpiry, ["scope"] = fixture.Scope
            };
            if (!refresh || !fixture.OmitRefreshIdentity) body["id_token"] = fixture.Identity(fields["client_id"], nonce, refresh);
            return Results.Json(body);
        });
        server.MapPost("/revoke", async (HttpContext context) =>
        {
            fixture.Revocations.Enqueue((await context.Request.ReadFormAsync(context.RequestAborted)).ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));
            return Results.StatusCode(fixture.RevocationStatus);
        });
        server.MapGet("/v1/models", (HttpContext context) =>
        {
            fixture.ModelAuthorizations.Enqueue(context.Request.Headers.Authorization.ToString());
            return Results.Json(new { models = new[] {
                new { slug = "fixture-z", display_name = "First choice", visibility = "list" },
                new { slug = "hidden", display_name = "Hidden", visibility = "hide" },
                new { slug = "fixture-a", display_name = "Second choice", visibility = "list" },
                new { slug = "fixture-z", display_name = "Duplicate", visibility = "list" } } });
        });
        server.MapPost("/v1/responses", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            fixture.InferenceRequests.Enqueue((context.Request.Headers.Authorization.ToString(), body.RootElement.Clone()));
            var number = fixture.InferenceRequests.Count;
            var output = number == 1
                ? "[{\"type\":\"function_call\",\"id\":\"item_edit\",\"call_id\":\"call_edit\",\"name\":\"edit\",\"namespace\":\"xamlg\",\"arguments\":\"{\\\"text\\\":\\\"changed\\\"}\",\"status\":\"completed\"}]"
                : "[{\"type\":\"message\",\"id\":\"message_done\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"Done\",\"annotations\":[]}]}]";
            using var items = JsonDocument.Parse(output);
            var result = new { type = "response.completed", sequence_number = number, response = new { id = "response_" + number,
                @object = "response", created_at = 123, model = "fixture-z", status = "completed", output = items.RootElement,
                usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 15 } } };
            return Results.Text("data: " + JsonSerializer.Serialize(result) + "\n\n", "text/event-stream");
        });
        await server.StartAsync(fixture.Token); return fixture;
    }
    public Task<ChatGptAccountManager> ManagerAsync() => ChatGptAccountManager.CreateAsync(new ChatGptFileCredentialStore(DirectoryPath), Options, cancellationToken: Token);
    public void RotateSigningKey() { _key = RSA.Create(2048); _keys.Add(_key); _keyId = Guid.NewGuid().ToString("N"); }
    private string Identity(string client, string? nonce, bool refresh)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = Origin.AbsoluteUri.TrimEnd('/'), ["sub"] = refresh ? RefreshSubject ?? "subject:" + client : "subject:" + client,
            ["aud"] = client, ["iat"] = now.ToUnixTimeSeconds(), ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            ["nonce"] = nonce, ["email"] = "fixture@example.test"
        };
        switch (IdentityFault)
        {
            case "issuer": payload["iss"] = "https://untrusted.example"; break;
            case "audience": payload["aud"] = "another-client"; break;
            case "nonce": payload["nonce"] = "another-nonce"; break;
            case "expired": payload["exp"] = now.AddMinutes(-5).ToUnixTimeSeconds(); break;
            case "future-issued": payload["iat"] = now.AddMinutes(5).ToUnixTimeSeconds(); break;
            case "missing-issued": payload.Remove("iat"); break;
            case "subject": payload["sub"] = ""; break;
            case "authorized-party": payload["azp"] = "another-client"; break;
            case "multiple-audiences": payload["aud"] = new[] { client, "another-client" }; break;
        }
        var encoded = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = _keyId })) + "." + Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return encoded + "." + Encode(_key.SignData(Encoding.ASCII.GetBytes(encoded), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    public async Task<Uri> LaunchAsync(ChatGptSignInLaunch launch)
    {
        Assert.DoesNotContain("id_token", launch.LaunchUrl); Assert.DoesNotContain("client_id", launch.LaunchUrl);
        using var response = await Http.GetAsync(launch.LaunchUrl, Token);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        return response.Headers.Location!;
    }
    public async Task<HttpStatusCode> CompleteAsync(Uri authorization)
    {
        using var authorize = await Http.GetAsync(authorization, Token);
        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);
        using var callback = await Http.GetAsync(authorize.Headers.Location, Token);
        return callback.StatusCode;
    }
    public async Task<ChatGptAccountInfo> SignInAsync(ChatGptAccountManager manager, bool remember = false, string? id = null, bool consent = false)
    {
        var launch = await manager.BeginSignInAsync(id, "Fixture account", remember, requestPlanConsent: consent, cancellationToken: Token);
        Assert.Equal(HttpStatusCode.OK, await CompleteAsync(await LaunchAsync(launch)));
        await UntilAsync(() => manager.State.SignIn?.Status == "completed");
        return manager.State.Accounts.Single(account => account.Id == manager.State.ActiveAccountId);
    }
    public async Task UntilAsync(Func<bool> predicate)
    { while (!predicate()) await Task.Delay(10, Token); }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async ValueTask DisposeAsync()
    {
        HoldRefresh?.TrySetResult(); Timeout.Cancel();
        await _server.StopAsync(CancellationToken.None); await _server.DisposeAsync(); Http.Dispose(); Timeout.Dispose();
        foreach (var key in _keys) key.Dispose();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
