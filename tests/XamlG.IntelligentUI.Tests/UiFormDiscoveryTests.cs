using System.Text.Json;
using XamlG.Automation;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiFormDiscoveryTests
{
    [Fact]
    public async Task Agents_receive_a_complete_publishable_form_and_submission_guidance()
    {
        var store = new UiSessionStore(); var catalog = new AutomationCatalog();
        using var ui = new UiAutomation(catalog, store);
        var discovery = await catalog.CallAsync("xamlg_ui_catalog", AutomationJson.Element(new { }), new("test", PrincipalId: "owner"));
        Assert.Contains("ui:SubmitButton", discovery.GetProperty("forms").GetString());
        Assert.Contains("innermost keyed row", discovery.GetProperty("syntax").GetString());
        var request = discovery.GetProperty("form").Deserialize<UiPublish>(AutomationJson.Options)!;
        var snapshot = store.Publish(request, "owner");
        Assert.Contains("Enter a project name.", snapshot.FallbackMarkdown);
        Assert.All(UiSessionStore.Flatten(snapshot.Roots), node => Assert.True(UiCatalog.Default.Components.ContainsKey(node.Type)));
        Assert.Equal("invalid_action", Assert.Throws<UiException>(() => store.PrepareAction(
            new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, "/submit"), "owner")).Code);
    }
}
