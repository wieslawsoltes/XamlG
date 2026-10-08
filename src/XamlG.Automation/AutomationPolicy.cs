using System.Collections.Frozen;

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

    public void Validate()
    {
        if (!Enum.IsDefined(Profile) || Scopes == null || Tools == null ||
            Scopes.Any(rule => !Enum.IsDefined(rule.Key) || !Enum.IsDefined(rule.Value)) ||
            Tools.Any(rule => string.IsNullOrWhiteSpace(rule.Key) || rule.Key.Length > 64 || !Enum.IsDefined(rule.Value)))
            throw new ArgumentException("Invalid permission policy.");
    }
    public PermissionDecision Decide(AutomationTool tool)
    {
        var exact = Tools.TryGetValue(tool.Name, out var toolRule) ? toolRule : (PermissionDecision?)null;
        var effects = tool.Effects.ToArray();
        if (exact == PermissionDecision.Deny || effects.Any(effect => Scopes.GetValueOrDefault(effect.Scope) == PermissionDecision.Deny))
            return PermissionDecision.Deny;
        if (Profile is PermissionProfile.ReadOnly or PermissionProfile.Plan && effects.Any(effect => effect.Effect != AutomationEffect.Read))
            return PermissionDecision.Deny;
        PermissionDecision DecideEffect(AutomationOperationEffect effect) => exact ??
            (Scopes.TryGetValue(effect.Scope, out var scopeRule) ? scopeRule : Profile switch
            {
                PermissionProfile.FullAccess => PermissionDecision.Allow,
                PermissionProfile.AutoEdit when effect.Effect != AutomationEffect.Execute && !tool.Destructive => PermissionDecision.Allow,
                _ when effect.Effect == AutomationEffect.Read => PermissionDecision.Allow,
                _ => PermissionDecision.Ask
            });
        var decision = effects.Any(effect => DecideEffect(effect) == PermissionDecision.Ask) ? PermissionDecision.Ask : PermissionDecision.Allow;
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
        ArgumentNullException.ThrowIfNull(policy); policy.Validate();
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(duration));
        // Copy rules so the caller cannot silently broaden an active run's authority.
        Policy = policy with { Scopes = policy.Scopes.ToFrozenDictionary(), Tools = policy.Tools.ToFrozenDictionary(StringComparer.Ordinal) };
        ExpiresAt = DateTimeOffset.UtcNow.Add(duration);
        _revocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _revocation.CancelAfter(duration);
    }
    public AutomationPolicy Policy { get; }
    public bool IsActive => !_revocation.IsCancellationRequested;
    public DateTimeOffset ExpiresAt { get; }
    public CancellationToken Token => _revocation.Token;
    public void Revoke() { try { _revocation.Cancel(); } catch (ObjectDisposedException) { } }
    public IReadOnlyList<string> GrantedTools { get { lock (_sync) return _toolGrants.Order(StringComparer.Ordinal).ToArray(); } }
    public bool RevokeTool(string name) { lock (_sync) return _toolGrants.Remove(name); }
    public void GrantTool(string name)
    {
        Token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A tool name is required.");
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
