using XamlG.Agents;
using XamlG.Automation;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AgentPermissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static AutomationTool Tool(bool destructive = false) => new("edit", "Edit", AutomationJson.Element(new { type = "object" }),
        AutomationScope.Designer, AutomationEffect.Edit, destructive, [new(AutomationScope.Source, AutomationEffect.Edit)]);

    [Fact]
    public void Combined_effect_denials_and_destructive_auto_edit_are_enforced()
    {
        var auto = new AutomationPolicy { Profile = PermissionProfile.AutoEdit };
        Assert.Equal(PermissionDecision.Allow, auto.Decide(Tool()));
        Assert.Equal(PermissionDecision.Ask, auto.Decide(Tool(true)));
        Assert.Equal(PermissionDecision.Deny, (auto with { NeverAsk = true }).Decide(Tool(true)));
        var denied = auto with { Profile = PermissionProfile.FullAccess, Scopes = new Dictionary<AutomationScope, PermissionDecision> { [AutomationScope.Source] = PermissionDecision.Deny },
            Tools = new Dictionary<string, PermissionDecision> { ["edit"] = PermissionDecision.Allow } };
        using var lease = new AutomationLease(denied, TimeSpan.FromMinutes(1));
        lease.GrantTool("edit"); Assert.Equal(PermissionDecision.Deny, lease.Decide(Tool()));
    }

    [Fact]
    public void Lease_rules_are_immutable_and_individual_grants_can_be_revoked()
    {
        using var lease = new AutomationLease(new(), TimeSpan.FromMinutes(1));
        Assert.IsNotType<Dictionary<string, PermissionDecision>>(lease.Policy.Tools);
        if (lease.Policy.Tools is IDictionary<string, PermissionDecision> rules)
            Assert.Throws<NotSupportedException>(() => rules["edit"] = PermissionDecision.Allow);
        lease.GrantTool("edit"); Assert.Equal(["edit"], lease.GrantedTools);
        Assert.Equal(PermissionDecision.Allow, lease.Decide(Tool()));
        Assert.True(lease.RevokeTool("edit")); Assert.Empty(lease.GrantedTools);
        Assert.Equal(PermissionDecision.Ask, lease.Decide(Tool()));
    }

    [Fact]
    public async Task Host_restrictions_and_fresh_full_access_acknowledgement_precede_provider_io()
    {
        var modes = new[] { PermissionProfile.Ask, PermissionProfile.FullAccess };
        using var harness = new AgentHarness(new AutomationCatalog(), constraints: new(allowedProfiles: modes, maximumLease: TimeSpan.FromMinutes(2)));
        modes[0] = PermissionProfile.AutoEdit;
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Done());
        var task = harness.CreateTask("Host limits", provider, "fixture", Token);
        var full = new AgentRunOptions { Policy = new() { Profile = PermissionProfile.FullAccess }, LeaseDuration = TimeSpan.FromMinutes(1) };
        await Assert.ThrowsAsync<AutomationException>(() => harness.RunAsync(task.Id, "Run", full, cancellationToken: Token));
        await Assert.ThrowsAsync<AutomationException>(() => harness.RunAsync(task.Id, "Run", full with { FullAccessAcknowledged = true, LeaseDuration = TimeSpan.FromMinutes(3) }, cancellationToken: Token));
        await Assert.ThrowsAsync<AutomationException>(() => harness.RunAsync(task.Id, "Run", full with { Policy = new() { Profile = PermissionProfile.AutoEdit } }, cancellationToken: Token));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.RunAsync(task.Id, "Run", full with { FullAccessAcknowledged = true, Policy = new() { Tools = new Dictionary<string, PermissionDecision> { ["invented_tool"] = PermissionDecision.Allow } } }, cancellationToken: Token));
        Assert.Empty(provider.Requests);
        await harness.RunAsync(task.Id, "Run", full with { FullAccessAcknowledged = true }, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Contains("\"maximumLease\":\"00:02:00\"", Assert.Single(provider.Requests).Instructions);
        await Assert.ThrowsAsync<AutomationException>(() => harness.RunAsync(task.Id, "Run again", full, cancellationToken: Token));
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Run_grant_ceiling_rejects_broad_approval_before_the_tool_runs()
    {
        var writes = 0;
        var catalog = new AutomationCatalog();
        catalog.Add<EmptyArguments, object>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit,
            (_, _) => { writes++; return ValueTask.FromResult<object>(new { }); });
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Call(new AgentToolCall("call", "edit", AutomationJson.Element(new { }))));
        using var harness = new AgentHarness(catalog, constraints: new(allowRunApprovals: false));
        var task = harness.CreateTask("Review", provider, "fixture", Token);
        await Assert.ThrowsAsync<AutomationException>(() => harness.RunAsync(task.Id, "Edit", new(),
            (_, _) => Task.FromResult(AgentApproval.AllowToolForRun), cancellationToken: Token));
        Assert.Equal(0, writes); Assert.Null(harness.ActivePermissions);
    }
    public sealed record EmptyArguments;
}
