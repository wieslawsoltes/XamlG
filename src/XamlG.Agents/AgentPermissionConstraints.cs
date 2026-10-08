using XamlG.Automation;

namespace XamlG.Agents;

/// <summary>Immutable host restrictions. Task preferences and remembered grants cannot broaden them.</summary>
public sealed class AgentPermissionConstraints
{
    public AgentPermissionConstraints(IEnumerable<PermissionProfile>? allowedProfiles = null,
        IEnumerable<AutomationScope>? deniedScopes = null, IEnumerable<string>? deniedTools = null,
        TimeSpan? maximumLease = null, bool allowRunApprovals = true)
    {
        AllowedProfiles = Array.AsReadOnly((allowedProfiles ?? Enum.GetValues<PermissionProfile>()).Distinct().ToArray());
        DeniedScopes = Array.AsReadOnly((deniedScopes ?? []).Distinct().ToArray());
        DeniedTools = Array.AsReadOnly((deniedTools ?? []).Distinct(StringComparer.Ordinal).ToArray());
        MaximumLease = maximumLease ?? TimeSpan.FromHours(1); AllowRunApprovals = allowRunApprovals;
        if (AllowedProfiles.Any(profile => !Enum.IsDefined(profile)) || DeniedScopes.Any(scope => !Enum.IsDefined(scope)) ||
            DeniedTools.Any(string.IsNullOrWhiteSpace) || MaximumLease < TimeSpan.FromSeconds(1) || MaximumLease > TimeSpan.FromHours(1))
            throw new ArgumentException("Invalid host permission constraints.");
    }
    public IReadOnlyList<PermissionProfile> AllowedProfiles { get; }
    public IReadOnlyList<AutomationScope> DeniedScopes { get; }
    public IReadOnlyList<string> DeniedTools { get; }
    public TimeSpan MaximumLease { get; }
    public bool AllowRunApprovals { get; }

    internal AutomationPolicy Apply(AgentRunOptions options, IReadOnlyList<AutomationTool> catalog, bool requireAcknowledgement)
    {
        var policy = options.Policy ?? throw new ArgumentException("A run policy is required.");
        policy.Validate();
        if (!AllowedProfiles.Contains(policy.Profile)) throw new AutomationException("permission_denied", "The host does not allow this permission profile.");
        if (options.LeaseDuration < TimeSpan.FromSeconds(1) || options.LeaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options), "Permission leases must last between one second and one hour.");
        if (options.LeaseDuration > MaximumLease)
            throw new AutomationException("permission_denied", "The selected lease exceeds the host's allowed duration.");
        if (requireAcknowledgement && policy.Profile == PermissionProfile.FullAccess && !options.FullAccessAcknowledged)
            throw new AutomationException("permission_denied", "A fresh Full Access acknowledgement is required for this run.");
        if (policy.Tools.Keys.Any(name => !catalog.Any(tool => tool.Name == name) && !DeniedTools.Contains(name)))
            throw new ArgumentException("An exact tool rule names a tool outside the current catalog.");
        var scopes = policy.Scopes.ToDictionary(); foreach (var scope in DeniedScopes) scopes[scope] = PermissionDecision.Deny;
        var tools = policy.Tools.ToDictionary(StringComparer.Ordinal); foreach (var tool in DeniedTools) tools[tool] = PermissionDecision.Deny;
        return policy with { Scopes = scopes, Tools = tools };
    }
}
