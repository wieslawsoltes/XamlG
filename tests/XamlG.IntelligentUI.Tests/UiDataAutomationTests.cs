using System.Text.Json;
using XamlG.Automation;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiDataAutomationTests
{
    private static JsonElement J(object value) => AutomationJson.Element(value);
    private static AutomationCallContext Context(string owner = "task-a") => new("test", TestContext.Current.CancellationToken, owner);
    [Fact]
    public async Task AuthorizedResultsRetainOriginalShapeAndAreOwnerScoped()
    {
        var catalog = new AutomationCatalog(); var store = new UiSessionStore();
        using var data = new UiDataAutomation(catalog, store, captureAuthorizedResult: tool => tool.Name == "read");
        catalog.Add<UiDataInventory, object>("read", "Read", AutomationScope.Source, AutomationEffect.Read,
            (_, _) => ValueTask.FromResult<object>(new { rows = new[] { new { name = "A", value = 1 }, new { name = "B", value = 2 } } }));
        var result = await catalog.CallAsync("read", J(new { }), Context());
        Assert.Single(result.EnumerateObject());
        var handle = Assert.Single(data.Data.List("task-a")); Assert.Empty(data.Data.List("task-b"));
        var page = await catalog.CallAsync("xamlg_ui_data_read", J(new UiDataResolve(new(handle.Id, handle.Version, "/rows", 1, 1, ["name"]))), Context());
        Assert.Equal("B", page.GetProperty("value")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("value", page.GetProperty("value")[0].EnumerateObject().Select(property => property.Name));
        await Assert.ThrowsAsync<AutomationException>(() => catalog.CallAsync("xamlg_ui_data_read", J(new UiDataResolve(new(handle.Id, handle.Version))), Context("task-b")).AsTask());
        store.Clear();
        var inventory = await catalog.CallAsync("xamlg_ui_data_list", J(new { }), Context());
        Assert.Equal(0, inventory.GetProperty("references").GetArrayLength());
    }
    [Fact]
    public async Task DeniedAndExecutingOperationsAreNeverCaptured()
    {
        var catalog = new AutomationCatalog((_, _) => ValueTask.FromResult(false));
        using var data = new UiDataAutomation(catalog, new(), captureAuthorizedResult: _ => true);
        catalog.Add<UiDataInventory, object>("read", "Read", AutomationScope.Source, AutomationEffect.Read, (_, _) => ValueTask.FromResult<object>(new { secret = 1 }));
        catalog.Add<UiDataInventory, object>("execute", "Execute", AutomationScope.Runtime, AutomationEffect.Execute, (_, _) => ValueTask.FromResult<object>(new { value = 2 }));
        await Assert.ThrowsAsync<AutomationException>(() => catalog.CallAsync("read", J(new { }), Context()).AsTask());
        await catalog.CallLocalAsync("execute", J(new { }), Context());
        Assert.Empty(data.Data.List("task-a"));
    }
    [Fact]
    public async Task BindingUsesTheSharedPermissionBoundary()
    {
        var catalog = new AutomationCatalog(); var store = new UiSessionStore();
        using var data = new UiDataAutomation(catalog, store);
        var snapshot = store.Publish(UiExamples.Pricing("price"), "task-a");
        var handle = data.Data.Put(J(new { price = 11 }), "task-a");
        var marker = await catalog.CallAsync("xamlg_ui_data_bind", J(new UiBindData("price", snapshot.Revision, [new("retained", new(handle.Id, handle.Version))])), Context());
        Assert.Equal(UiPresentation.FormatName, marker.GetProperty("format").GetString());
        Assert.Equal(11, store.Read("price", "task-a").Data.GetProperty("retained").GetProperty("price").GetInt32());
    }
}
