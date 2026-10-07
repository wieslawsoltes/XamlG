using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace XamlG.Agents.OpenAI;

/// <summary>Reusable local-account service. Owns OAuth, validated registration identities and
/// serialized token rotation. Tasks capture a registration ID; switching the picker cannot
/// silently change the account funding an existing task.</summary>
public sealed class ChatGptAccountManager : IAsyncDisposable
{
    private readonly IChatGptCredentialStore _store;
    private readonly ChatGptAccountOptions _options;
    private readonly HttpClient _http;
    internal HttpClient InferenceHttp { get; } = new(new ChatGptInferenceHandler()) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private readonly bool _ownsHttp;
    private readonly ChatGptOAuthProtocol _protocol;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private string _hostId = "", _active = "";
    private string? _storageError;
    private Pending? _pending;
    private bool _disposed, _savePending;
    private int _disposeStarted;
    private ChatGptAccountManager(IChatGptCredentialStore store, ChatGptAccountOptions options, HttpClient? http)
    {
        _store = store; _options = options; _ownsHttp = http == null;
        _http = http ?? new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _protocol = new(_http, options);
    }
    public static async Task<ChatGptAccountManager> CreateAsync(IChatGptCredentialStore store,
        ChatGptAccountOptions? options = null, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ChatGptAccountManager? result = null;
        try
        {
            options ??= new();
            options.Validate();
            result = new ChatGptAccountManager(store, options, http);
            var state = await store.ReadAsync(cancellationToken);
            if (state != null)
            {
                if (state.Version != 1 || state.HostId == null || !state.HostId.StartsWith("urn:uuid:", StringComparison.Ordinal) || !Guid.TryParse(state.HostId[9..], out _) || state.Accounts == null || state.Accounts.Length > 32)
                    throw new ChatGptAccountException("invalid_credential_store");
                if (state.AuthenticationOrigin != result._options.AuthenticationOrigin.AbsoluteUri || state.ApiEndpoint != result._options.ApiEndpoint.AbsoluteUri)
                    throw new ChatGptAccountException("credential_store_endpoint_mismatch");
                result._hostId = state.HostId;
                foreach (var account in state.Accounts)
                {
                    ValidateStored(account);
                    if (!account.Remember) account.Tokens = null;
                    if (result._accounts.Values.Any(existing => existing.Record.ClientId == account.ClientId) || !result._accounts.TryAdd(account.Id, new(account))) throw new ChatGptAccountException("duplicate_account_registration");
                }
                if (state.ActiveAccountId != null && result._accounts.ContainsKey(state.ActiveAccountId)) result._active = state.ActiveAccountId;
            }
            else { result._hostId = "urn:uuid:" + Guid.NewGuid(); await result.SaveAsync(cancellationToken); }
            return result;
        }
        catch
        {
            if (result != null) await result.DisposeAsync();
            else store.Dispose();
            throw;
        }
    }
    public ChatGptAccountState State
    {
        get { lock (_sync) return new(_active.Length == 0 ? null : _active, _accounts.Values.Select(Info).ToArray(), _pending?.Info, _storageError); }
    }
    private static ChatGptAccountInfo Info(Account account)
    {
        var record = account.Record;
        return new(record.Id, record.Label, record.Email, record.ClientId,
        record.Tokens != null, record.Tokens?.Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal) == true,
        record.Remember, record.Tokens?.ExpiresAt, account.LastFailure);
    }
    internal void RecordFailure(string id, string code, AgentProviderFailure? details)
    { if (details != null) lock (_sync) if (_accounts.TryGetValue(id, out var account)) account.LastFailure = new(code, details, DateTimeOffset.UtcNow); }
    public ChatGptAccountAgentProvider CreateProvider(string? accountId = null)
    {
        lock (_sync)
        {
            var account = Find(accountId ?? _active);
            if (account.Record.Tokens == null || account.Lifetime.IsCancellationRequested) throw new ChatGptAccountException("sign_in_required");
            return new(this, account.Record.Id, account.Record.Label, _options.ApiEndpoint);
        }
    }
    internal CancellationToken SessionLifetime(string id)
    {
        lock (_sync)
        {
            var account = Find(id);
            if (account.Record.Tokens == null || account.Lifetime.IsCancellationRequested) throw new ChatGptAccountException("sign_in_required");
            return account.Lifetime.Token;
        }
    }
    public async Task<ChatGptSignInLaunch> BeginSignInAsync(string? accountId, string? label, bool remember,
        string? retrySignInId = null, bool requestPlanConsent = false, CancellationToken ownerSession = default, CancellationToken cancellationToken = default)
    {
        if (label != null && (string.IsNullOrWhiteSpace(label) || label.Length > 100 || label.Any(char.IsControl))) throw new ArgumentException("Use a label of 1–100 characters.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed(); ownerSession.ThrowIfCancellationRequested();
            Pending? previous; lock (_sync) previous = _pending;
            if (previous != null && !previous.Loopback.Completion.IsCompleted) throw new ChatGptAccountException("sign_in_already_pending");
            if (retrySignInId != null && (previous == null || previous.Id != retrySignInId || !previous.Info.CanRetryRegistration || accountId != null))
                throw new ChatGptAccountException("registration_retry_unavailable");
            var retryClientId = retrySignInId == null ? null : previous!.ClientId;
            if (retryClientId != null) { label = previous!.Label; remember = previous.Remember; }
            if (previous != null) await previous.Loopback.DisposeAsync();
            Account? returning; lock (_sync) returning = accountId == null ? null : Find(accountId);
            if (requestPlanConsent && returning == null) throw new ArgumentException("Choose an existing registration before enabling plan usage.");
            if (returning == null && _accounts.Count >= 32) throw new ChatGptAccountException("account_registration_limit");
            await _protocol.DiscoverAsync(cancellationToken);
            var state = ChatGptOAuthProtocol.RandomValue(); var nonce = ChatGptOAuthProtocol.RandomValue(); var verifier = ChatGptOAuthProtocol.RandomValue();
            Pending? pending = null;
            var loopback = new ChatGptLoopbackSignIn(state, query => CompleteSignInAsync(pending!, query), ownerSession, _lifetime.Token);
            pending = new(Guid.NewGuid().ToString("N"), returning?.Record.Id, label, remember, nonce, verifier, loopback);
            pending.ClientId = returning?.Record.ClientId ?? retryClientId;
            var fields = new Dictionary<string, string>
            {
                ["client_id"] = pending.ClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = _hostId,
                ["response_type"] = "code", ["redirect_uri"] = loopback.RedirectUri, ["scope"] = ChatGptOAuthProtocol.Scope,
                ["resource"] = ChatGptOAuthProtocol.Resource, ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256",
                ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            };
            if (pending.ClientId == null) fields["agent_name_hint"] = _options.AgentName;
            if (requestPlanConsent) fields["prompt"] = "consent";
            if (returning != null)
            {
                if (returning.Record.Tokens?.IdToken is { } hint) fields["id_token_hint"] = hint;
                if (returning.Record.Email is { } email) fields["login_hint"] = email;
            }
            var authorize = new Uri(_options.AuthenticationOrigin, "/api/accounts/authorize").AbsoluteUri + "?" +
                string.Join("&", fields.Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)));
            lock (_sync) _pending = pending;
            loopback.Start(authorize); _ = ObserveSignInAsync(pending);
            return new(pending.Id, loopback.LaunchUrl, pending.ExpiresAt);
        }
        finally { _gate.Release(); }
    }
    private async Task ObserveSignInAsync(Pending pending)
    {
        await pending.Loopback.Completion;
        lock (_sync)
        {
            pending.Nonce = ""; pending.Verifier = "";
            if (ReferenceEquals(pending, _pending) && pending.Status is "waiting" or "exchanging")
            { pending.Status = "cancelled"; pending.ErrorCode = DateTimeOffset.UtcNow >= pending.ExpiresAt ? "sign_in_expired" : "sign_in_cancelled"; }
        }
    }
    private async Task<bool> CompleteSignInAsync(Pending pending, IReadOnlyDictionary<string, string> query)
    {
        await _gate.WaitAsync(_lifetime.Token);
        ChatGptTokens? issued = null; string? clientId = null;
        try
        {
            pending.Loopback.CancellationToken.ThrowIfCancellationRequested();
            lock (_sync) pending.Status = "exchanging";
            if (query.ContainsKey("error")) throw new ChatGptAccountException(query.GetValueOrDefault("error") == "access_denied" ? "consent_declined" : "authorization_failed");
            Account? returning; lock (_sync) returning = pending.AccountId == null ? null : Find(pending.AccountId);
            clientId = query.GetValueOrDefault("client_id") ?? pending.ClientId;
            if (!ClientIdValid(clientId) || pending.ClientId != null && clientId != pending.ClientId) throw new ChatGptAccountException("invalid_issued_client_id");
            lock (_sync) pending.ClientId = clientId;
            var code = query.GetValueOrDefault("code");
            if (string.IsNullOrEmpty(code) || code.Length > 8192 || code.Any(char.IsControl)) throw new ChatGptAccountException("invalid_authorization_code");
            // Complete the accepted token exchange under the service lifetime. A vanished browser
            // cannot interrupt rotation halfway through and leave only an obsolete token on disk.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var exchange = await _protocol.ExchangeAsync(clientId!, code, pending.Verifier, pending.Loopback.RedirectUri, pending.Nonce, timeout.Token);
            issued = exchange.Tokens;
            if (returning != null && returning.Record.Subject != exchange.Subject) throw new ChatGptAccountException("account_identity_changed");
            pending.Loopback.CancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                var existing = _accounts.Values.FirstOrDefault(account => account.Record.ClientId == clientId);
                if (existing != null && existing.Record.Subject != exchange.Subject) throw new ChatGptAccountException("account_identity_changed");
                var id = returning?.Record.Id ?? existing?.Record.Id ?? Guid.NewGuid().ToString("N");
                var record = new ChatGptStoredAccount { Id = id, ClientId = clientId!, Subject = exchange.Subject, Email = exchange.Email,
                    Label = pending.Label ?? returning?.Record.Label ?? existing?.Record.Label ?? "Account " + id[..6], Remember = pending.Remember, Tokens = issued };
                if (_accounts.TryGetValue(id, out var old)) old.End();
                _accounts[id] = new(record); _active = id; _savePending = true;
                issued = null; // The account service now owns these credentials, even if saving fails.
            }
            await SaveAsync(_lifetime.Token);
            lock (_sync) { pending.Status = "completed"; pending.ErrorCode = null; }
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (issued != null && clientId != null)
            { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await _protocol.RevokeAsync(clientId, issued.RefreshToken, timeout.Token); }
            lock (_sync) { pending.Status = "failed"; pending.ErrorCode = ErrorCode(error); }
            return false;
        }
        finally { _gate.Release(); }
    }
    public async Task CancelSignInAsync(string id, CancellationToken cancellationToken = default)
    {
        Pending pending;
        lock (_sync) { pending = _pending?.Id == id ? _pending : throw new ChatGptAccountException("unknown_sign_in"); pending.Loopback.Cancel(); }
        await pending.Loopback.Completion.WaitAsync(cancellationToken);
    }
    public async Task SelectAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { lock (_sync) { _ = Find(id); _active = id; } await SaveAsync(cancellationToken); }
        finally { _gate.Release(); }
    }
    public async Task ConfigureAsync(string id, string label, bool remember, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 100 || label.Any(char.IsControl)) throw new ArgumentException("Use a label of 1–100 characters.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            lock (_sync) { var account = Find(id); account.Record.Label = label; account.Record.Remember = remember; }
            await SaveAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }
    public async Task<ChatGptSignOutResult> SignOutAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Account account; lock (_sync)
            {
                account = Find(id); account.End();
                if (_pending?.AccountId == id && _pending.Status is "waiting" or "exchanging") _pending.Loopback.Cancel();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var confirmed = await _protocol.RevokeAsync(account.Record.ClientId, account.Record.Tokens?.RefreshToken, timeout.Token);
            lock (_sync) account.Record.Tokens = null;
            await SaveAsync(_lifetime.Token);
            return new(confirmed);
        }
        finally { _gate.Release(); }
    }
    internal async Task<AccessLease> AccessAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed(); Account account; lock (_sync) account = Find(id);
            var tokens = account.Record.Tokens ?? throw new ChatGptAccountException("sign_in_required");
            if (!tokens.Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal)) throw new ChatGptAccountException("chatgpt_plan_permission_required");
            if (tokens.IdentityValidationRequired) await ValidateRefreshAsync(account);
            if (DateTimeOffset.UtcNow >= tokens.ExpiresAt.AddMinutes(-2))
            {
                if (tokens.EarliestRefreshAt > DateTimeOffset.UtcNow)
                { if (DateTimeOffset.UtcNow >= tokens.ExpiresAt) throw new ChatGptAccountException("refresh_not_yet_available", true); }
                else
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        tokens = await _protocol.RefreshAsync(account.Record, timeout.Token);
                        lock (_sync) { account.Record.Tokens = tokens; _savePending = true; }
                        // Persist rotation before a potentially unavailable JWKS fetch. A retry
                        // validates this replacement; it must never reuse the old refresh token.
                        await SaveAsync(_lifetime.Token);
                        if (tokens.IdentityValidationRequired) await ValidateRefreshAsync(account);
                    }
                    catch (ChatGptAccountException error) when (error.Code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused" or "account_identity_changed")
                    { lock (_sync) { account.End(); account.Record.Tokens = null; } await SaveAsync(_lifetime.Token); throw; }
                }
            }
            if (_savePending) await SaveAsync(_lifetime.Token);
            if (!tokens.Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal)) throw new ChatGptAccountException("chatgpt_plan_permission_required");
            cancellationToken.ThrowIfCancellationRequested();
            return new(tokens.AccessToken, account.Lifetime.Token);
        }
        finally { _gate.Release(); }
    }
    private async Task ValidateRefreshAsync(Account account)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _protocol.ValidateRefreshedIdentityAsync(account.Record, timeout.Token);
            lock (_sync) { account.Record.Tokens!.IdentityValidationRequired = false; _savePending = true; }
            await SaveAsync(_lifetime.Token);
        }
        catch (ChatGptAccountException error) when (error.Code is "invalid_id_token" or "invalid_authorized_party" or "invalid_id_token_identity" or "account_identity_changed" or "missing_id_token")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _protocol.RevokeAsync(account.Record.ClientId, account.Record.Tokens?.RefreshToken, timeout.Token);
            lock (_sync) { account.End(); account.Record.Tokens = null; }
            await SaveAsync(_lifetime.Token); throw;
        }
    }
    public async Task<IReadOnlyList<ChatGptModel>> ListModelsAsync(string id, CancellationToken cancellationToken = default)
    {
        var access = await AccessAsync(id, cancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, access.Lifetime);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.ApiEndpoint, "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
        using var body = await ChatGptOAuthProtocol.JsonAsync(response, lifetime.Token);
        if (!body.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 1000) throw new ChatGptAccountException("invalid_model_catalog");
        var result = new List<ChatGptModel>(); var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            if (ChatGptOAuthProtocol.OptionalString(model, "visibility", 64) != "list") continue;
            var slug = ChatGptOAuthProtocol.String(model, "slug", 200);
            if (slugs.Add(slug)) result.Add(new(slug, ChatGptOAuthProtocol.String(model, "display_name", 200)));
        }
        return result;
    }
    private async Task SaveAsync(CancellationToken token)
    {
        ChatGptStoredState state;
        lock (_sync) { _savePending = true; state = new() { HostId = _hostId, AuthenticationOrigin = _options.AuthenticationOrigin.AbsoluteUri, ApiEndpoint = _options.ApiEndpoint.AbsoluteUri,
            ActiveAccountId = _active.Length == 0 ? null : _active, Accounts = _accounts.Values.Select(account => account.Record).ToArray() }; }
        try { await _store.WriteAsync(state, token); lock (_sync) { _savePending = false; _storageError = null; } }
        catch (Exception error) when (error is not OutOfMemoryException)
        { lock (_sync) _storageError = "credential_storage_failed"; throw new ChatGptAccountException("credential_storage_failed"); }
    }
    private Account Find(string id) => _accounts.TryGetValue(id, out var account) ? account : throw new ChatGptAccountException("select_chatgpt_account");
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(ChatGptAccountManager)); }
    private static bool ClientIdValid(string? value) => value is { Length: > 7 and <= 256 } && value.StartsWith("oaiapp_", StringComparison.Ordinal) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static void ValidateStored(ChatGptStoredAccount? record)
    {
        if (record == null || !Guid.TryParseExact(record.Id, "N", out _) || !ClientIdValid(record.ClientId) || string.IsNullOrEmpty(record.Subject) || record.Subject.Length > 1024 ||
            string.IsNullOrWhiteSpace(record.Label) || record.Label.Length > 100 || record.Label.Any(char.IsControl) || record.Email?.Length > 320)
            throw new ChatGptAccountException("invalid_credential_store");
        if (record.Tokens is { } tokens && (string.IsNullOrEmpty(tokens.AccessToken) || tokens.AccessToken.Length > 32768 || tokens.AccessToken.Any(char.IsWhiteSpace) ||
            tokens.RefreshToken?.Length > 32768 || tokens.IdToken?.Length > 32768 || tokens.Scopes == null || tokens.Scopes.Length > 100 ||
            tokens.Scopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Length > 256 || scope.Any(char.IsWhiteSpace))))
            throw new ChatGptAccountException("invalid_credential_store");
    }
    private static string ErrorCode(Exception error) => error switch
    { ChatGptAccountException account => account.Code, OperationCanceledException => "sign_in_cancelled_or_timed_out", HttpRequestException => "connection_error", _ => "sign_in_failed" };
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true; _lifetime.Cancel();
        if (_pending != null) await _pending.Loopback.DisposeAsync();
        await _gate.WaitAsync();
        try
        {
            lock (_sync) { foreach (var account in _accounts.Values) { account.End(); account.Record.Tokens = null; } _accounts.Clear(); }
            _store.Dispose(); InferenceHttp.Dispose(); if (_ownsHttp) _http.Dispose();
        }
        finally { _gate.Release(); _gate.Dispose(); _lifetime.Dispose(); }
    }
    internal sealed class AccessLease(string token, CancellationToken lifetime)
    { internal string Token { get; } = token; internal CancellationToken Lifetime { get; } = lifetime; }
    private sealed class Account(ChatGptStoredAccount record)
    {
        internal ChatGptStoredAccount Record { get; } = record;
        internal ChatGptRequestFailure? LastFailure { get; set; }
        internal CancellationTokenSource Lifetime { get; } = new();
        internal void End() { if (!Lifetime.IsCancellationRequested) Lifetime.Cancel(); Lifetime.Dispose(); }
    }
    private sealed class Pending(string id, string? accountId, string? label, bool remember, string nonce, string verifier, ChatGptLoopbackSignIn loopback)
    {
        internal string Id { get; } = id;
        internal string? AccountId { get; } = accountId;
        internal string? Label { get; } = label;
        internal bool Remember { get; } = remember;
        internal string Nonce { get; set; } = nonce;
        internal string Verifier { get; set; } = verifier;
        internal string? ClientId { get; set; }
        internal ChatGptLoopbackSignIn Loopback { get; } = loopback;
        internal DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddMinutes(10);
        internal string Status { get; set; } = "waiting";
        internal string? ErrorCode { get; set; }
        internal ChatGptSignInInfo Info => new(Id, Status, ExpiresAt, ErrorCode,
            AccountId == null && ClientId != null && Status is "failed" or "cancelled");
    }
}
