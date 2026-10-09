using System.Text.Json;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiDataReferenceTests
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    [Fact] public async Task PagesAndProjectsOwnerScopedImmutableData()
    {
        var token = TestContext.Current.CancellationToken; var store = new UiDataStore();
        var handle = store.Put(J(new { rows = Enumerable.Range(0, 20).Select(id => new { id, name = "Row " + id, extra = "hidden" }) }), "owner", "Search results");
        var page = await store.ResolveAsync(new(handle.Id, handle.Version, "/rows", 3, 2, ["id", "name"]), "owner", 1024, token);
        Assert.Equal(20, page.Total); Assert.True(page.HasMore); Assert.Equal(3, page.Value[0].GetProperty("id").GetInt32()); Assert.False(page.Value[0].TryGetProperty("extra", out _));
        await Assert.ThrowsAsync<UiException>(() => store.ResolveAsync(new(handle.Id, handle.Version), "other", 1024, token).AsTask());
        await Assert.ThrowsAsync<UiException>(() => store.ResolveAsync(new(handle.Id, handle.Version + 1), "owner", 1024, token).AsTask());
        Assert.False(store.Release(new(handle.Id, handle.Version), "other")); Assert.True(store.Release(new(handle.Id, handle.Version), "owner")); Assert.Empty(store.List("owner"));
    }
    [Theory]
    [InlineData("/a~1b/~0value/0", "7")]
    [InlineData("/", "8")]
    public void JsonPointersImplementEscapesAndEmptyKeys(string pointer, string expected)
    {
        using var json = JsonDocument.Parse("{\"a/b\":{\"~value\":[7]},\"\":8}");
        Assert.Equal(expected, UiJsonPointer.Resolve(json.RootElement, pointer).GetRawText());
    }
    [Theory]
    [InlineData("/00")]
    [InlineData("/+0")]
    [InlineData("/-")]
    [InlineData("/0~2")]
    [InlineData("#/0")]
    public void RejectsAmbiguousIndicesAndInvalidPointerEscapes(string pointer) => Assert.Throws<UiException>(() => UiJsonPointer.Resolve(J(new[] { 1 }), pointer));
    [Fact] public async Task ReferenceBindingPreservesUserStateAndRejectsRetiredWorkspaces()
    {
        var token = TestContext.Current.CancellationToken; var store = new UiSessionStore(); var first = store.Publish(UiExamples.Pricing(), "owner");
        var data = new UiDataStore(); var handle = data.Put(J(new { rows = new[] { "A", "B" } }), "owner");
        var result = await store.BindDataAsync(new(first.Id, first.Revision, [new("rows", new(handle.Id, handle.Version, "/rows"))]), "owner", data, token);
        Assert.Equal(2, result.Data.GetProperty("rows").GetArrayLength()); Assert.True(JsonElement.DeepEquals(first.State, result.State));
        var delayed = new DelayedResolver(); var pending = store.BindDataAsync(new(result.Id, result.Revision, [new("other", new(handle.Id, handle.Version))]), "owner", delayed, token).AsTask();
        store.Clear(); delayed.Complete(new(J(new { }), null, false, handle));
        await Assert.ThrowsAsync<UiException>(() => pending); Assert.Empty(store.ListLocal());
    }
    [Fact] public async Task ExpiryAndBudgetsRemainEnforced()
    {
        var token = TestContext.Current.CancellationToken; var time = new Clock(); var store = new UiDataStore(maximumEntries: 1, timeProvider: time);
        var first = store.Put(J(new { text = "value" }), "owner", lifetime: TimeSpan.FromSeconds(1));
        Assert.Throws<UiException>(() => store.Put(J(new { }), "owner"));
        time.Now += TimeSpan.FromSeconds(2);
        await Assert.ThrowsAsync<UiException>(() => store.ResolveAsync(new(first.Id, first.Version), "owner", 512, token).AsTask());
        var next = store.Put(J(new { text = "value" }), "owner");
        await Assert.ThrowsAsync<UiException>(() => store.ResolveAsync(new(next.Id, next.Version), "owner", 2, token).AsTask());
        Assert.NotEqual(first.Id, next.Id);
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class DelayedResolver : IUiDataResolver
    {
        private readonly TaskCompletionSource<UiDataPage> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(UiDataPage page) => _result.SetResult(page);
        public async ValueTask<UiDataPage> ResolveAsync(UiDataReference reference, string principal, int maximumBytes, CancellationToken cancellationToken = default) => await _result.Task.WaitAsync(cancellationToken);
    }
}
