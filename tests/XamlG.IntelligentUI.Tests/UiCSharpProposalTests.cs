using XamlG.Automation;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiCSharpProposalTests
{
    [Fact]
    public async Task ProposingDoesNotExecuteAndStatusRemainsOwnerScoped()
    {
        var store = new UiSessionStore(); var proposals = new UiCSharpProposals(store); var catalog = new AutomationCatalog(); proposals.Register(catalog);
        var context = new AutomationCallContext("test", TestContext.Current.CancellationToken, "task-a");
        var request = new UiPublish("full", 0, 1, "<TextBlock xmlns=\"https://github.com/avaloniaui\" Text=\"{ui:Expr System.Environment.Exit(1)}\"/>");
        var result = await catalog.CallAsync("xamlg_ui_csharp_propose", AutomationJson.Element(request), context);
        Assert.Equal("pending", result.GetProperty("status").GetString());
        var proposal = Assert.Single(proposals.SnapshotLocal());
        await Assert.ThrowsAsync<AutomationException>(() => catalog.CallAsync("xamlg_ui_csharp_status", AutomationJson.Element(new UiCSharpProposalRead(proposal.Id)), context with { PrincipalId = "other" }).AsTask());
        Assert.Throws<UiException>(() => proposals.CompleteLocal(proposal.Id, "wrong hash", null, null));
        store.Clear(); Assert.Empty(proposals.SnapshotLocal());
    }
}
