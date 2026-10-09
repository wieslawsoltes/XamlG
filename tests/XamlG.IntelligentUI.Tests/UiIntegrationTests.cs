using System.Text.Json;
using Avalonia.Headless.XUnit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Automation;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiIntegrationTests
{
    private static readonly AutomationCallContext Owner = new("test", PrincipalId: "owner");
    private static JsonElement J<T>(T value) => AutomationJson.Element(value);

    [Fact] public async Task CatalogCarriesUiResourceMetadataAndPreservesAuthority()
    {
        var catalog = new AutomationCatalog(); var store = new UiSessionStore();
        using var registration = new UiAutomation(catalog, store);
        var present = Assert.Single(catalog.Tools, tool => tool.Name == "xamlg_ui_present");
        Assert.Equal(UiAutomation.ResourceUri, present.Metadata!.Value.GetProperty("ui").GetProperty("resourceUri").GetString());
        Assert.Equal(AutomationEffect.Edit, present.Effect);
        var marker = await catalog.CallAsync(present.Name, J(UiExamples.Pricing()), Owner);
        Assert.True(UiPresentation.TryRead(marker.GetRawText(), out var presentation));
        Assert.Contains("$232", presentation!.FallbackMarkdown);
        var snapshot = await catalog.CallAsync("xamlg_ui_read", J(new UiRead("pricing")), Owner);
        Assert.Equal(presentation.SessionId, snapshot.GetProperty("sessionId").GetString());
        var resource = await catalog.ReadResourceAsync(UiAutomation.ResourceUri, Owner);
        Assert.Contains("ui/initialize", resource);
        Assert.Contains("connect-src 'none'", resource);
        Assert.DoesNotContain("eval(", resource);
        Assert.DoesNotContain("innerHTML", resource);
        await Assert.ThrowsAsync<AutomationException>(() => catalog.CallAsync("xamlg_ui_read", J(new UiRead("pricing")), new("other", PrincipalId: "other")).AsTask());
    }
    [Fact] public async Task UiRegistrationNeverBypassesCatalogApproval()
    {
        var catalog = new AutomationCatalog((_, _) => ValueTask.FromResult(false)); var store = new UiSessionStore();
        using var registration = new UiAutomation(catalog, store);
        await Assert.ThrowsAsync<AutomationException>(() => catalog.CallAsync("xamlg_ui_present", J(UiExamples.Pricing()), Owner).AsTask());
        Assert.Null(store.ReadLocal("pricing"));
    }
    [Fact] public async Task MissingTransportPrincipalIsRejected()
    {
        var catalog = new AutomationCatalog(); using var registration = new UiAutomation(catalog, new());
        await Assert.ThrowsAsync<AutomationException>(() => catalog.CallAsync("xamlg_ui_present", J(UiExamples.Pricing()), new("untrusted caller name")).AsTask());
    }
    [Fact] public void ReleaseAndRecreateCannotReuseOldMutationRevisions()
    {
        var store = new UiSessionStore(); var first = store.Publish(UiExamples.Pricing(), "owner");
        store.Release(new(first.Id, first.Revision), "owner");
        var second = store.Publish(UiExamples.Pricing(), "owner");
        Assert.NotEqual(first.SessionId, second.SessionId); Assert.True(second.Revision > first.Revision);
        Assert.Throws<UiException>(() => store.ChangeState(new(first.Id, first.Revision, first.StateRevision, "seats", J(10)), "owner"));
        store.Clear(); var third = store.Publish(UiExamples.Pricing(), "owner");
        Assert.True(third.Revision > second.Revision);
    }
    [AvaloniaFact] public void OldNativeTranscriptCardDoesNotAttachToReplacementSession()
    {
        var store = new UiSessionStore(); var first = store.Publish(UiExamples.Pricing(), "owner");
        using var native = UiAvaloniaSession.CreateLocal(store, UiPresentation.From(first));
        Assert.Equal(first.SessionId, native.Snapshot!.SessionId);
        store.Release(new(first.Id, first.Revision), "owner");
        store.Publish(UiExamples.Pricing(), "different-owner");
        native.Refresh(); Assert.Null(native.Snapshot); Assert.NotNull(native.Diagnostic);
    }
    [AvaloniaFact] public void NativeSessionUpdatesReactiveStateAndDetachesOnDispose()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(UiExamples.Pricing(), "owner");
        var native = new UiAvaloniaSession(store, UiPresentation.From(snapshot), "owner");
        native.ChangeState(new(snapshot.Id, snapshot.Revision, 0, "seats", J(10)));
        Assert.Contains("$290", native.Snapshot!.FallbackMarkdown);
        native.Dispose(); Assert.Null(native.Snapshot);
        store.ChangeData(new(snapshot.Id, snapshot.Revision, J(new { unitPrice = 30 })), "owner");
        Assert.Null(native.Snapshot);
    }
    [Fact] public void ExportsResolvedXamlAndReactiveCSharpWithoutModelCodeExecution()
    {
        var snapshot = new UiSessionStore().Publish(UiExamples.Pricing(), "owner");
        var xaml = UiSourceExporter.Xaml(snapshot);
        Assert.Contains("$232", xaml); Assert.DoesNotContain("ui:Expr", xaml);
        var source = UiSourceExporter.CSharp(snapshot);
        Assert.Contains("UiSessionStore", source); Assert.Contains("UiAvaloniaSession", source);
        Assert.DoesNotContain("AvaloniaXamlLoader", source);
        Assert.Empty(CSharpSyntaxTree.ParseText(source).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }
    [Fact] public void ConditionalMissingActionIsRejectedBeforeStatePublication()
    {
        var store = new UiSessionStore();
        var request = UiExamples.Pricing() with { Xaml = "<StackPanel xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\"><CheckBox ui:Bind=\"annual\"/><Button ui:When=\"{ui:Expr state.annual}\" ui:Action=\"missing\"/></StackPanel>", Actions = [] };
        var first = store.Publish(request, "owner");
        Assert.Throws<UiException>(() => store.ChangeState(new(first.Id, first.Revision, 0, "annual", J(true)), "owner"));
        Assert.False(store.Read(first.Id, "owner").State.GetProperty("annual").GetBoolean());
    }
    [Fact] public void ToolNamesCannotBeEmpty()
    {
        Assert.Throws<UiException>(() => new UiSessionStore().Publish(UiExamples.Pricing() with { Actions = [new("bad", "tool", Tool: "")] }, "owner"));
    }
    [Fact] public async Task ResourceNotificationsReflectCommittedChanges()
    {
        var catalog = new AutomationCatalog(); var store = new UiSessionStore();
        using var registration = new UiAutomation(catalog, store);
        var notifications = new List<string>(); catalog.ResourceChanged += notifications.Add;
        var first = store.Publish(UiExamples.Pricing(), "owner");
        store.ChangeState(new(first.Id, first.Revision, 0, "seats", J(9)), "owner");
        Assert.Equal(2, notifications.Count); Assert.All(notifications, uri => Assert.Equal("xamlg://ui/pricing", uri));
        var text = await catalog.ReadResourceAsync(notifications[0], Owner);
        Assert.Contains("$261", text);
    }
}
