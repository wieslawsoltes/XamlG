using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiPersistenceTests
{
    [Fact] public void ArchiveRestoresReactiveStateOwnershipAndSessionIdentity()
    {
        var source = new UiSessionStore(); var request = UiExamples.Pricing(); var initial = source.Publish(request, "owner");
        var key = initial.State.EnumerateObject().First(field => field.Value.ValueKind == JsonValueKind.Number).Name;
        source.ChangeState(new(initial.Id, initial.Revision, initial.StateRevision, key, JsonSerializer.SerializeToElement(9)), "owner");
        var current = source.Read(initial.Id, "owner"); var archive = source.CaptureArchive("workspace");
        var restored = new UiSessionStore(); restored.RestoreArchive(archive, "workspace");
        var actual = restored.Read(initial.Id, "owner");
        Assert.Equal(current.SessionId, actual.SessionId); Assert.Equal(current.StateRevision, actual.StateRevision); Assert.Equal(current.Revision, actual.Revision);
        Assert.Equal(current.FallbackMarkdown, actual.FallbackMarkdown); Assert.True(JsonElement.DeepEquals(current.State, actual.State));
        Assert.Throws<UiException>(() => restored.Read(initial.Id, "other"));
        var updated = restored.Publish(request with { ExpectedRevision = actual.Revision, Sequence = actual.Sequence + 1 }, "owner");
        Assert.True(updated.Revision > actual.Revision); Assert.Equal(actual.SessionId, updated.SessionId);
    }
    [Fact] public void FailedRestoreNeverPartiallyPublishesOrOverwritesExistingSessions()
    {
        var source = new UiSessionStore(); source.Publish(UiExamples.Pricing(), "owner");
        var archive = source.CaptureArchive("workspace"); var target = new UiSessionStore();
        Assert.Throws<UiException>(() => target.RestoreArchive(archive, "other")); Assert.Empty(target.ListLocal());
        var json = JsonNode.Parse(archive)!.AsObject(); var second = json["surfaces"]![0]!.DeepClone(); second["id"] = "bad"; second["sessionId"] = Guid.NewGuid().ToString("N"); second["xaml"] = "<Bad/>";
        json["surfaces"]!.AsArray().Add(second);
        Assert.Throws<UiException>(() => target.RestoreArchive(json.ToJsonString(), "workspace")); Assert.Empty(target.ListLocal());
        target.RestoreArchive(archive, "workspace");
        Assert.Throws<UiException>(() => target.RestoreArchive(archive, "workspace")); Assert.Single(target.ListLocal());
    }
    [Fact] public void ArchiveCannotRestoreToolGrantsOrChangeTheExpressionBackend()
    {
        var source = new UiSessionStore(); source.Publish(UiExamples.Pricing(), "owner");
        var json = JsonNode.Parse(source.CaptureArchive("workspace"))!.AsObject();
        json["expressionLanguage"] = "csharp-full";
        Assert.Throws<UiException>(() => new UiSessionStore().RestoreArchive(json.ToJsonString(), "workspace"));
        json["expressionLanguage"] = "csharp-pure"; json["grants"] = new JsonArray("execute");
        Assert.Throws<UiException>(() => new UiSessionStore().RestoreArchive(json.ToJsonString(), "workspace"));
    }
    [Fact] public void ReleasedSessionIdsAreNotReusedAfterRestore()
    {
        var source = new UiSessionStore(); var initial = source.Publish(UiExamples.Pricing(), "owner");
        var target = new UiSessionStore(); target.RestoreArchive(source.CaptureArchive("workspace"), "workspace");
        target.Release(new(initial.Id, initial.Revision), "owner"); var next = target.Publish(UiExamples.Pricing(), "owner");
        Assert.NotEqual(initial.SessionId, next.SessionId); Assert.True(next.Revision > initial.Revision);
        Assert.Throws<UiException>(() => target.ChangeData(new(initial.Id, initial.Revision, initial.Data), "owner"));
    }
    [Fact] public async Task FileStorageAndCoordinatorRejectLostUpdatesAndSupportForget()
    {
        var token = TestContext.Current.CancellationToken; var directory = Path.Combine(Path.GetTempPath(), "xamlg-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new UiFileArchiveStorage(directory); var store = new UiSessionStore();
            var first = new UiWorkspacePersistence(store, storage, "workspace"); Assert.False(await first.LoadAsync(token));
            store.Publish(UiExamples.Pricing(), "owner"); await first.SaveAsync(token); Assert.False(first.HasUnsavedChanges);
            var other = new UiSessionStore(); var second = new UiWorkspacePersistence(other, new UiFileArchiveStorage(directory), "workspace");
            Assert.True(await second.LoadAsync(token)); Assert.Single(other.List("owner"));
            var snapshot = store.ListLocal()[0]; store.ChangeData(new(snapshot.Id, snapshot.Revision, snapshot.Data), "owner"); await first.SaveAsync(token);
            other.Clear(); await Assert.ThrowsAsync<UiException>(() => second.SaveAsync(token).AsTask());
            await first.ForgetAsync(token); Assert.Null(await storage.ReadAsync("workspace", token));
            Assert.DoesNotContain(Directory.EnumerateFiles(directory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    [Fact] public void NullInputStateCanBeInitializedAndClearedThroughItsTypedControl()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(new("nullable", 0, 1, "<NumericUpDown xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\" ui:Bind=\"value\"/>", JsonSerializer.SerializeToElement(new { value = (decimal?)null })), "owner");
        var edited = store.ChangeState(new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, "value", JsonSerializer.SerializeToElement(10)), "owner");
        Assert.Equal(10, edited.State.GetProperty("value").GetInt32());
        var cleared = store.ChangeState(new(edited.Id, edited.Revision, edited.StateRevision, "value", JsonSerializer.SerializeToElement<object?>(null)), "owner");
        Assert.Equal(JsonValueKind.Null, cleared.State.GetProperty("value").ValueKind);
    }
}
