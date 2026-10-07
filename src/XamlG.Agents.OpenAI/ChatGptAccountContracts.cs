namespace XamlG.Agents.OpenAI;

public sealed record ChatGptAccountInfo(string Id, string Label, string? Email, string ClientId,
    bool SignedIn, bool PlanEnabled, bool Remember, DateTimeOffset? ExpiresAt, ChatGptRequestFailure? LastFailure);
public sealed record ChatGptRequestFailure(string Code, AgentProviderFailure Details, DateTimeOffset Time);
public sealed record ChatGptSignInInfo(string Id, string Status, DateTimeOffset ExpiresAt, string? ErrorCode, bool CanRetryRegistration);
public sealed record ChatGptAccountState(string? ActiveAccountId, IReadOnlyList<ChatGptAccountInfo> Accounts,
    ChatGptSignInInfo? SignIn, string? StorageError);
public sealed record ChatGptSignInLaunch(string Id, string LaunchUrl, DateTimeOffset ExpiresAt);
public sealed record ChatGptSignOutResult(bool RemoteRevocationConfirmed);
public sealed record ChatGptModel(string Slug, string DisplayName);

/// <summary>Host-only credential storage. Implementations must serialize access across processes,
/// protect secrets, and replace the complete snapshot atomically. Never expose this data in UI,
/// MCP resources, telemetry or transcripts. Only Remember=true token sets may be persisted.</summary>
public interface IChatGptCredentialStore : IDisposable
{
    Task<ChatGptStoredState?> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(ChatGptStoredState state, CancellationToken cancellationToken = default);
}
public sealed class ChatGptStoredState
{
    public int Version { get; init; } = 1;
    public required string HostId { get; init; }
    public string AuthenticationOrigin { get; init; } = "https://auth.openai.com/";
    public string ApiEndpoint { get; init; } = "https://api.openai.com/v1/";
    public string? ActiveAccountId { get; init; }
    public required ChatGptStoredAccount[] Accounts { get; init; }
}
public sealed class ChatGptStoredAccount
{
    public required string Id { get; init; }
    public required string Label { get; set; }
    public required string ClientId { get; init; }
    public required string Subject { get; init; }
    public string? Email { get; init; }
    public bool Remember { get; set; }
    public ChatGptTokens? Tokens { get; set; }
}
/// <summary>Secret host-only material; deliberately has no value-printing record ToString.</summary>
public sealed class ChatGptTokens
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? IdToken { get; init; }
    public required string[] Scopes { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? EarliestRefreshAt { get; init; }
    public bool IdentityValidationRequired { get; set; }
}
public sealed class ChatGptAccountException(string code, bool retryable = false)
    : Exception("ChatGPT account: " + code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public AgentProviderFailure? Details { get; init; }
}

/// <summary>Production endpoints are fixed to the documented public route. HTTP loopback is
/// accepted for deterministic embedding tests, never arbitrary remote OAuth/inference hosts.</summary>
public sealed record ChatGptAccountOptions
{
    public string AgentName { get; init; } = "XamlG Studio";
    public Uri AuthenticationOrigin { get; init; } = new("https://auth.openai.com");
    public Uri ApiEndpoint { get; init; } = new("https://api.openai.com/v1/");
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(AgentName) || AgentName.Length > 100 || AgentName.Any(char.IsControl)) throw new ArgumentException("Use a bounded application name.");
        Check(AuthenticationOrigin, "https://auth.openai.com/", origin: true); Check(ApiEndpoint, "https://api.openai.com/v1/", origin: false);
        if (AuthenticationOrigin.Scheme != ApiEndpoint.Scheme || AuthenticationOrigin.Scheme == "http" &&
            AuthenticationOrigin.GetLeftPart(UriPartial.Authority) != ApiEndpoint.GetLeftPart(UriPartial.Authority))
            throw new ArgumentException("Use production endpoints together or a single loopback fixture origin.");
        static void Check(Uri value, string production, bool origin)
        {
            if (value == null || !value.IsAbsoluteUri || value.UserInfo.Length != 0 || value.Query.Length != 0 || value.Fragment.Length != 0 ||
                value.AbsoluteUri != production && !(value.Scheme == "http" && value.Host == "127.0.0.1") ||
                origin && value.AbsolutePath != "/" || !value.AbsolutePath.EndsWith('/'))
                throw new ArgumentException("Use the documented OpenAI endpoint or an explicit HTTP loopback fixture.");
        }
    }
}
