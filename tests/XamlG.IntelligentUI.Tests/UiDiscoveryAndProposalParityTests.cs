using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XamlG.Automation;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiDiscoveryAndProposalParityTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Discovery_exposes_native_and_composite_schemas_and_executable_examples()
    {
        var catalog = new AutomationCatalog(); var store = new UiSessionStore();
        using var ui = new UiAutomation(catalog, store);
        var result = await catalog.CallAsync("xamlg_ui_catalog", J(new { }), new("test", PrincipalId: "owner"));
        Assert.Equal(42, result.GetProperty("components").GetArrayLength());
        Assert.Equal(18, result.GetProperty("composites").GetArrayLength());
        Assert.Contains(result.GetProperty("composites").EnumerateArray(), component =>
            component.GetProperty("name").GetString() == "ui:SubmitButton" && component.GetProperty("supportsAction").GetBoolean());
        Assert.Equal(UiCatalog.UiNamespace, result.GetProperty("namespaces").GetProperty("ui").GetString());
        foreach (var name in new[] { "example", "localActions", "dashboard" })
        {
            var request = result.GetProperty(name).Deserialize<UiPublish>(AutomationJson.Options)!;
            var snapshot = store.Publish(request, "owner");
            Assert.NotEmpty(snapshot.Roots);
            Assert.All(UiSessionStore.Flatten(snapshot.Roots), node => Assert.True(UiCatalog.Default.Components.ContainsKey(node.Type)));
        }
    }

    [Fact]
    public async Task Proposals_preserve_and_hash_local_action_code_without_compiling_or_executing()
    {
        var store = new UiSessionStore(); var catalog = new AutomationCatalog();
        var proposals = new UiCSharpProposals(store); proposals.Register(catalog);
        var request = UiInteractionExamples.Counter() with
        {
            Actions = [new("increment", "state", Arguments: J(new { count = "{ui:Expr throw new InvalidOperationException(\"must not execute\")}" })), new("reset", "state", Arguments: J(new { count = 0 }))]
        };
        var result = await catalog.CallAsync("xamlg_ui_csharp_propose", AutomationJson.Element(request), new("test", PrincipalId: "owner"));
        var proposal = result.Deserialize<UiCSharpProposal>(AutomationJson.Options)!;
        Assert.Equal("pending", proposal.Status);
        Assert.Contains("must not execute", proposal.Request.Actions![0].Arguments!.Value.GetRawText());
        Assert.Empty(store.List("owner"));
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(proposal.Request, AutomationJson.Options))));
        Assert.Equal(expected, proposal.Sha256);
        var changed = await catalog.CallAsync("xamlg_ui_csharp_propose", AutomationJson.Element(request with { Actions = [new("increment", "state", Arguments: J(new { count = 12 }))] }), new("test", PrincipalId: "owner"));
        Assert.NotEqual(proposal.Sha256, changed.GetProperty("sha256").GetString());
        Assert.Throws<UiException>(() => proposals.CompleteLocal(proposal.Id, changed.GetProperty("sha256").GetString()!, null, null));
    }

    [Theory]
    [InlineData("copy")][InlineData("message")][InlineData("openUrl")][InlineData("tool")]
    public void Proposal_normalization_never_grants_external_action_authority(string kind)
    {
        var request = UiInteractionExamples.Counter() with { Actions = [new("increment", kind, Text: "external", Tool: kind == "tool" ? "xamlg_source_read" : null)] };
        Assert.Equal("invalid_proposal", Assert.Throws<UiException>(() => UiCSharpDeclaration.Normalize(request)).Code);
    }

    [Fact]
    public void Local_action_review_rejects_empty_duplicate_undeclared_and_oversized_patches()
    {
        foreach (var actions in new UiAction[][]
        {
            [new("increment", "state", Arguments: J(new { }))],
            [new("increment", "state", Arguments: J(new { missing = 1 }))],
            [new("increment", "state", Arguments: J(new { count = 1 })), new("increment", "state", Arguments: J(new { count = 2 }))],
            [new("increment", "state", Arguments: J(new { count = new string('x', 16385) }))]
        }) Assert.Throws<UiException>(() => UiCSharpDeclaration.Normalize(UiInteractionExamples.Counter() with { Actions = actions }));
    }
}
