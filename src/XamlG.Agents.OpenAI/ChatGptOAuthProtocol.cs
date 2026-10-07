using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace XamlG.Agents.OpenAI;

internal sealed class ChatGptOAuthProtocol(HttpClient http, ChatGptAccountOptions options)
{
    internal const string Resource = "https://api.openai.com/v1";
    internal const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private JsonWebKeySet? _keys;
    private DateTimeOffset _keysAt;
    private Metadata? _metadata;
    private readonly SemaphoreSlim _metadataGate = new(1);
    internal async Task<Metadata> DiscoverAsync(CancellationToken token)
    {
        await _metadataGate.WaitAsync(token);
        try
        {
            if (_metadata != null) return _metadata;
            using var response = await http.GetAsync(new Uri(options.AuthenticationOrigin, "/.well-known/openid-configuration"), HttpCompletionOption.ResponseHeadersRead, token);
            using var body = await JsonAsync(response, token);
            var issuer = String(body.RootElement, "issuer", 2048);
            if (issuer != options.AuthenticationOrigin.AbsoluteUri.TrimEnd('/')) throw new ChatGptAccountException("invalid_oidc_issuer");
            return _metadata = new(issuer, AuthUri(String(body.RootElement, "jwks_uri", 2048)), AuthUri(String(body.RootElement, "revocation_endpoint", 2048)));
        }
        finally { _metadataGate.Release(); }
    }
    private Uri AuthUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != options.AuthenticationOrigin.GetLeftPart(UriPartial.Authority) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new ChatGptAccountException("invalid_oidc_endpoint");
        return uri;
    }
    internal async Task<(ChatGptTokens Tokens, string Subject, string? Email)> ExchangeAsync(string clientId, string code,
        string verifier, string redirectUri, string nonce, CancellationToken token)
    {
        var result = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirectUri, ["resource"] = Resource }, null, token);
        try
        {
            if (result.IdToken == null) throw new ChatGptAccountException("missing_id_token");
            var identity = await ValidateIdentityAsync(result.IdToken, clientId, nonce, token);
            return (result, identity.Subject, identity.Email);
        }
        catch
        { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await RevokeAsync(clientId, result.RefreshToken, timeout.Token); throw; }
    }
    internal async Task<ChatGptTokens> RefreshAsync(ChatGptStoredAccount account, CancellationToken token)
    {
        var previous = account.Tokens ?? throw new ChatGptAccountException("sign_in_required");
        if (previous.RefreshToken == null) throw new ChatGptAccountException("sign_in_required");
        return await TokenAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId,
            ["refresh_token"] = previous.RefreshToken, ["resource"] = Resource }, previous, token);
    }
    internal async Task ValidateRefreshedIdentityAsync(ChatGptStoredAccount account, CancellationToken token)
    {
        var identity = await ValidateIdentityAsync(account.Tokens!.IdToken ?? throw new ChatGptAccountException("missing_id_token"), account.ClientId, null, token);
        if (identity.Subject != account.Subject) throw new ChatGptAccountException("account_identity_changed");
    }
    private async Task<ChatGptTokens> TokenAsync(Dictionary<string, string> fields, ChatGptTokens? previous, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.AuthenticationOrigin, "/api/accounts/oauth/token")) { Content = new FormUrlEncodedContent(fields) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        using var body = await JsonAsync(response, token); var root = body.RootElement;
        if (!String(root, "token_type", 64).Equals("Bearer", StringComparison.OrdinalIgnoreCase)) throw new ChatGptAccountException("unsupported_token_type");
        var expiry = root.GetProperty("expires_in").GetInt64();
        if (expiry is < 1 or > 86400) throw new ChatGptAccountException("invalid_token_expiry");
        var scope = OptionalString(root, "scope", 8192);
        var scopes = scope == null ? previous?.Scopes ?? throw new ChatGptAccountException("missing_granted_scopes") : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        var refresh = OptionalString(root, "refresh_token", 32768);
        if (scopes.Contains("offline_access", StringComparer.Ordinal) && refresh == null) throw new ChatGptAccountException("missing_rotated_refresh_token");
        DateTimeOffset? earliest = null;
        if (root.TryGetProperty("earliest_refresh_at", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch)) earliest = DateTimeOffset.FromUnixTimeSeconds(epoch);
            else if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)) earliest = date;
            else throw new ChatGptAccountException("invalid_refresh_time");
        }
        var idToken = OptionalString(root, "id_token", 32768) ?? previous?.IdToken;
        return new() { AccessToken = String(root, "access_token", 32768), RefreshToken = refresh,
            IdToken = idToken, IdentityValidationRequired = previous != null && idToken != previous.IdToken, Scopes = scopes,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiry), EarliestRefreshAt = earliest };
    }
    private async Task<(string Subject, string? Email)> ValidateIdentityAsync(string encoded, string clientId, string? nonce, CancellationToken token)
    {
        var metadata = await DiscoverAsync(token);
        var handler = new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 32768 };
        if (!handler.CanReadToken(encoded)) throw new ChatGptAccountException("invalid_id_token");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await _metadataGate.WaitAsync(token);
            try
            {
                if (_keys == null || DateTimeOffset.UtcNow - _keysAt > TimeSpan.FromHours(1) || attempt != 0)
                {
                    using var response = await http.GetAsync(metadata.Keys, HttpCompletionOption.ResponseHeadersRead, token);
                    using var body = await JsonAsync(response, token);
                    _keys = new(body.RootElement.GetRawText()); _keysAt = DateTimeOffset.UtcNow;
                    if (_keys.Keys.Count is < 1 or > 100) throw new ChatGptAccountException("invalid_signing_keys");
                }
            }
            finally { _metadataGate.Release(); }
            var validation = await handler.ValidateTokenAsync(encoded, new TokenValidationParameters
            {
                ValidIssuer = metadata.Issuer, ValidAudience = clientId, ValidateIssuer = true, ValidateAudience = true,
                RequireSignedTokens = true, RequireExpirationTime = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
                IssuerSigningKeys = _keys.GetSigningKeys(), ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
                ClockSkew = TimeSpan.FromSeconds(60), IncludeTokenOnFailedValidation = false, LogTokenId = false
            });
            if (!validation.IsValid)
            {
                if (attempt == 0 && validation.Exception is SecurityTokenSignatureKeyNotFoundException) continue;
                throw new ChatGptAccountException("invalid_id_token");
            }
            var jwt = (JsonWebToken)validation.SecurityToken;
            if (nonce != null && (!jwt.TryGetPayloadValue<string>("nonce", out var returnedNonce) || !Equal(nonce, returnedNonce))) throw new ChatGptAccountException("invalid_id_token_nonce");
            if (jwt.Audiences.Count() > 1 && !jwt.TryGetPayloadValue<string>("azp", out _) ||
                jwt.TryGetPayloadValue<string>("azp", out var authorizedParty) && authorizedParty != clientId) throw new ChatGptAccountException("invalid_authorized_party");
            if (string.IsNullOrEmpty(jwt.Subject) || jwt.Subject.Length > 1024 || !jwt.TryGetPayloadValue<long>("iat", out _) || jwt.IssuedAt > DateTime.UtcNow.AddSeconds(60)) throw new ChatGptAccountException("invalid_id_token_identity");
            var email = jwt.TryGetPayloadValue<string>("email", out var address) && address.Length <= 320 && !address.Any(char.IsControl) ? address : null;
            return (jwt.Subject, email);
        }
        throw new ChatGptAccountException("invalid_id_token");
    }
    internal async Task<bool> RevokeAsync(string clientId, string? refreshToken, CancellationToken token)
    {
        if (refreshToken == null) return false;
        try
        {
            var metadata = await DiscoverAsync(token);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, metadata.Revocation)
                    { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId, ["token"] = refreshToken, ["token_type_hint"] = "refresh_token" }) };
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode == HttpStatusCode.OK) return true;
                    if ((int)response.StatusCode < 500) return false;
                }
                catch (HttpRequestException) { }
                if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ChatGptAccountException or JsonException) { }
        return false;
    }
    internal static async Task<JsonDocument> JsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        var bytes = await ReadBoundedAsync(response.Content, 1024 * 1024, token);
        if (!response.IsSuccessStatusCode)
        {
            var failure = Failure(response, bytes);
            throw new ChatGptAccountException(failure.Code, response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) { Details = failure.Details };
        }
        try { return JsonDocument.Parse(bytes, new() { MaxDepth = 32 }); }
        catch (JsonException) { throw new ChatGptAccountException("invalid_response_json"); }
    }
    internal static (string Code, AgentProviderFailure Details) Failure(HttpResponseMessage response, byte[] bytes)
    {
        var code = "http_" + (int)response.StatusCode; string? parameter = null; var shape = bytes.Length == 0 ? "empty" : "non-json";
        try
        {
            using var body = JsonDocument.Parse(bytes, new() { MaxDepth = 32 }); var root = body.RootElement;
            shape = "json-" + root.ValueKind.ToString().ToLowerInvariant();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                shape = "error-" + error.ValueKind.ToString().ToLowerInvariant();
                var candidate = error.ValueKind == JsonValueKind.String ? error.GetString() :
                    error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
                if (candidate is { Length: > 0 and <= 128 } && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')) code = candidate;
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.String) parameter = SafeMetadata(param.GetString());
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out _)) shape = "detail";
        }
        catch (JsonException) { }
        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? SafeMetadata(values.FirstOrDefault()) : null;
        return (code, new((int)response.StatusCode, shape, requestId, parameter));
        static string? SafeMetadata(string? value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '[' or ']' or ':') ? value : null;
    }
    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new ChatGptAccountException("response_too_large");
        await using var source = await content.ReadAsStreamAsync(token); using var buffer = new MemoryStream(); var chunk = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(chunk, token); if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > maximum) throw new ChatGptAccountException("response_too_large");
            buffer.Write(chunk, 0, count);
        }
    }
    internal static string String(JsonElement root, string name, int maximum) => OptionalString(root, name, maximum) ?? throw new ChatGptAccountException("missing_" + name);
    internal static string? OptionalString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.Length > maximum || text.Any(char.IsControl)) throw new ChatGptAccountException("invalid_" + name);
        return text;
    }
    internal static string RandomValue() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    internal static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
    internal sealed record Metadata(string Issuer, Uri Keys, Uri Revocation);
}
