using System.Text.Json;
using XamlG.Automation;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AutomationCatalogTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":123,\"expectedRevision\":0}")]
    [InlineData("{\"text\":\"x\",\"expectedRevision\":0,\"extra\":true}")]
    [InlineData("{\"text\":\"x\",\"text\":\"y\",\"expectedRevision\":0}")]
    [InlineData("{\"text\":\"x\",\"expectedRevision\":1.5}")]
    [InlineData("[]")]
    public async Task Invalid_arguments_never_reach_authorization_or_execution(string json)
    {
        var authorized = false; var executed = false;
        var catalog = new AutomationCatalog((_, _) => { authorized = true; return ValueTask.FromResult(true); });
        catalog.Add<Edit, string>("edit", "Edit source", AutomationScope.Source, AutomationEffect.Edit,
            (args, _) => { executed = true; return ValueTask.FromResult(args.Text); });
        using var document = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<AutomationException>(async () => await catalog.CallAsync("edit", document.RootElement, new("test")));
        Assert.False(authorized); Assert.False(executed);
    }

    [Fact]
    public async Task Denied_and_cancelled_calls_do_not_execute()
    {
        var executed = false;
        var catalog = new AutomationCatalog((_, _) => ValueTask.FromResult(false));
        catalog.Add<Edit, bool>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit,
            (_, _) => { executed = true; return ValueTask.FromResult(true); });
        var args = AutomationJson.Element(new Edit("text", 0));
        var error = await Assert.ThrowsAsync<AutomationException>(async () => await catalog.CallAsync("edit", args, new("test")));
        Assert.Equal("permission_denied", error.Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await catalog.CallAsync("edit", args, new("test", cancellation.Token)));
        Assert.False(executed);
    }

    [Fact]
    public void Denies_and_profile_ceilings_dominate_full_access_and_run_grants()
    {
        var tool = new AutomationTool("run", "Run", AutomationJson.Element(new { type = "object" }), AutomationScope.Runtime, AutomationEffect.Execute);
        var full = new AutomationPolicy { Profile = PermissionProfile.FullAccess, Scopes = new Dictionary<AutomationScope, PermissionDecision> { [AutomationScope.Runtime] = PermissionDecision.Deny } };
        using var lease = new AutomationLease(full, TimeSpan.FromMinutes(1));
        lease.GrantTool("run");
        Assert.Equal(PermissionDecision.Deny, lease.Decide(tool));
        var read = new AutomationPolicy { Profile = PermissionProfile.ReadOnly, Tools = new Dictionary<string, PermissionDecision> { ["run"] = PermissionDecision.Allow } };
        Assert.Equal(PermissionDecision.Deny, read.Decide(tool));
        Assert.Equal(PermissionDecision.Deny, new AutomationPolicy { NeverAsk = true }.Decide(tool));
        lease.Revoke();
        Assert.ThrowsAny<OperationCanceledException>(() => lease.Decide(tool));
    }

    [Fact]
    public void Run_authority_copies_rules_and_does_not_leak_grants()
    {
        var rules = new Dictionary<string, PermissionDecision> { ["edit"] = PermissionDecision.Ask };
        var policy = new AutomationPolicy { Tools = rules };
        using var first = new AutomationLease(policy, TimeSpan.FromMinutes(1));
        using var second = new AutomationLease(policy, TimeSpan.FromMinutes(1));
        var tool = new AutomationTool("edit", "Edit", AutomationJson.Element(new { }), AutomationScope.Source, AutomationEffect.Edit);
        rules["edit"] = PermissionDecision.Allow;
        Assert.Equal(PermissionDecision.Ask, first.Decide(tool));
        first.GrantTool("edit");
        Assert.Equal(PermissionDecision.Allow, first.Decide(tool));
        Assert.Equal(PermissionDecision.Ask, second.Decide(tool));
    }

    public sealed record Edit(string Text, long ExpectedRevision);
}
