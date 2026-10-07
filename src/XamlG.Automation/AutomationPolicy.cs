namespace XamlG.Automation;

/// <summary>Deny rules dominate grants. Never-ask means deny when review would be required.</summary>
public sealed record AutomationPolicy
{
    public PermissionProfile Profile { get; init; } = PermissionProfile.Ask;
    public IReadOnlyDictionary<AutomationScope, PermissionDecision> Scopes { get; init; } =
        new Dictionary<AutomationScope, PermissionDecision>();
    public IReadOnlyDictionary<string, PermissionDecision> Tools { get; init; } =
        new Dictionary<string, PermissionDecision>(StringComparer.Ordinal);
    public bool NeverAsk { get; init; }

    public PermissionDecision Decide(AutomationTool tool)
    {
        var exact = Tools.TryGetValue(tool.Name, out var toolRule) ? toolRule : (PermissionDecision?)null;
        var scope = Scopes.TryGetValue(tool.Scope, out var scopeRule) ? scopeRule : (PermissionDecision?)null;
        if (exact == PermissionDecision.Deny || scope == PermissionDecision.Deny) return PermissionDecision.Deny;
        if (Profile is PermissionProfile.ReadOnly or PermissionProfile.Plan && tool.Effect != AutomationEffect.Read)
            return PermissionDecision.Deny;
        var decision = exact ?? scope ?? Profile switch
        {
            PermissionProfile.FullAccess => PermissionDecision.Allow,
            PermissionProfile.AutoEdit when tool.Effect != AutomationEffect.Execute => PermissionDecision.Allow,
            _ when tool.Effect == AutomationEffect.Read => PermissionDecision.Allow,
            _ => PermissionDecision.Ask
        };
        return NeverAsk && decision == PermissionDecision.Ask ? PermissionDecision.Deny : decision;
    }
}

/// <summary>One run's authority. Dispose/revoke cancels I/O, reviews and queued operations.</summary>
public sealed class AutomationLease : IDisposable
{
    private readonly CancellationTokenSource _revocation;
    private readonly HashSet<string> _toolGrants = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    public AutomationLease(AutomationPolicy policy, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(duration));
        // Copy rules so the caller cannot silently broaden an active run's authority.
        Policy = policy with { Scopes = policy.Scopes.ToDictionary(), Tools = policy.Tools.ToDictionary(StringComparer.Ordinal) };
        ExpiresAt = DateTimeOffset.UtcNow.Add(duration);
        _revocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _revocation.CancelAfter(duration);
    }
    public AutomationPolicy Policy { get; }
    public DateTimeOffset ExpiresAt { get; }
    public CancellationToken Token => _revocation.Token;
    public void Revoke() { try { _revocation.Cancel(); } catch (ObjectDisposedException) { } }
    public void GrantTool(string name)
    {
        Token.ThrowIfCancellationRequested();
        lock (_sync) _toolGrants.Add(name);
    }
    public PermissionDecision Decide(AutomationTool tool)
    {
        Token.ThrowIfCancellationRequested();
        var result = Policy.Decide(tool);
        lock (_sync) return result == PermissionDecision.Ask && _toolGrants.Contains(tool.Name) ? PermissionDecision.Allow : result;
    }
    public void Dispose() { _revocation.Cancel(); _revocation.Dispose(); }
}
