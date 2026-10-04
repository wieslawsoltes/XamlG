using Microsoft.AspNetCore.Components;
using XamlG.Playground;
using XamlG.Playground.Editing;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class EditorInteropLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReplacingAnEditorDoesNotDisposeTheModuleOrASibling()
    {
        var js = new EditorInteropFixture();
        await using var owner = new EditorInteropModule(js);
        var firstCallback = new EditorCallbackLifetime(); var secondCallback = new EditorCallbackLifetime();
        var first = js.Create(owner, firstCallback, "first"); var second = js.Create(owner, secondCallback, "second");
        await Task.WhenAll(first.Ready, second.Ready).WaitAsync(Deadline);
        await first.DisposeAsync();
        Assert.True(first.IsRetired); Assert.Equal(1, firstCallback.Disposals);
        Assert.Equal("second", await second.ReadAsync<string>("getEditorText"));
        var replacementCallback = new EditorCallbackLifetime(); var replacement = js.Create(owner, replacementCallback, "replacement");
        await replacement.Ready.WaitAsync(Deadline);
        Assert.Equal("replacement", await replacement.ReadAsync<string>("getEditorText"));
        Assert.Equal(1, js.Imports); Assert.Equal(0, js.Source.Disposals); Assert.Equal(0, js.Facade.Disposals);
        await owner.DisposeAsync(); await owner.DisposeAsync();
        Assert.Empty(js.Buffers); Assert.Equal(1, secondCallback.Disposals); Assert.Equal(1, replacementCallback.Disposals);
        Assert.Equal(1, js.Source.Disposals); Assert.Equal(1, js.Facade.Disposals);
    }

    [Fact]
    public async Task RetirementDuringCreationCleansTheLateHandleWithoutPublishingIt()
    {
        var js = new EditorInteropFixture { ReleaseCreation = EditorInteropFixture.NewBarrier() };
        await using var owner = new EditorInteropModule(js);
        var callback = new EditorCallbackLifetime(); var session = js.Create(owner, callback);
        try
        {
            await js.CreationStarted.Task.WaitAsync(Deadline);
            var disposal = session.DisposeAsync().AsTask();
            Assert.True(session.IsRetired); Assert.False(disposal.IsCompleted);
            Assert.Null(await session.ReadAsync<string>("getEditorText"));
            js.ReleaseCreation.TrySetResult();
            await disposal.WaitAsync(Deadline);
            Assert.Empty(js.Buffers); Assert.Equal(1, callback.Disposals);
            Assert.Equal(new[] { "create:1", "disposeEditor:1" }, js.Events.ToArray());
        }
        finally { js.ReleaseCreation.TrySetResult(); }
    }

    [Fact]
    public async Task RetirementWaitsForAnAdmittedReadAndDiscardsItsLateResult()
    {
        var js = new EditorInteropFixture { ReleaseRead = EditorInteropFixture.NewBarrier() };
        await using var owner = new EditorInteropModule(js);
        var callback = new EditorCallbackLifetime(); var session = js.Create(owner, callback);
        await session.Ready.WaitAsync(Deadline);
        try
        {
            var read = session.ReadAsync<string>("getEditorText");
            await js.ReadStarted.Task.WaitAsync(Deadline);
            var disposal = session.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted); Assert.Equal(0, callback.Disposals); Assert.Single(js.Buffers);
            Assert.Null(await session.ReadAsync<string>("getEditorText"));
            js.ReleaseRead.TrySetResult();
            Assert.Null(await read.WaitAsync(Deadline));
            await disposal.WaitAsync(Deadline); await session.DisposeAsync();
            Assert.Empty(js.Buffers); Assert.Equal(1, callback.Disposals);
            Assert.Equal(new[] { "create:1", "getEditorText:1", "disposeEditor:1" }, js.Events.ToArray());
        }
        finally { js.ReleaseRead.TrySetResult(); }
    }

    [Fact]
    public async Task LiveCaptureReturnsPendingUserTextRatherThanTheCreationParameter()
    {
        var js = new EditorInteropFixture(); await using var owner = new EditorInteropModule(js);
        var session = js.Create(owner, new EditorCallbackLifetime(), "parameter");
        await session.Ready;
        js.Buffers[1] = "pending user text";
        Assert.Equal("pending user text", await session.ReadAsync<string>("getEditorText"));
    }

    [Fact]
    public async Task ALiveInteropFailureIsNotReplacedWithCachedSourceText()
    {
        var js = new EditorInteropFixture { FailRead = true }; await using var owner = new EditorInteropModule(js);
        var session = js.Create(owner, new EditorCallbackLifetime());
        await session.Ready;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadAsync<string>("getEditorText"));
        Assert.Equal("Live read failed.", error.Message);
    }

    [Fact]
    public async Task FailedCreationStillReleasesTheCallbackExactlyOnce()
    {
        var js = new EditorInteropFixture { FailCreation = true }; await using var owner = new EditorInteropModule(js);
        var callback = new EditorCallbackLifetime(); var session = js.Create(owner, callback);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Ready);
        await session.DisposeAsync(); await session.DisposeAsync();
        Assert.Equal(1, callback.Disposals); Assert.Empty(js.Buffers);
    }

    [Fact]
    public async Task FailedCleanupDoesNotKeepTheManagedCallbackAlive()
    {
        var js = new EditorInteropFixture { FailCleanup = true }; await using var owner = new EditorInteropModule(js);
        var callback = new EditorCallbackLifetime(); var session = js.Create(owner, callback);
        await session.Ready;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask());
        Assert.Equal(1, callback.Disposals); Assert.True(session.IsRetired);
        Assert.Null(await session.ReadAsync<string>("getEditorText"));
    }

    [Fact]
    public async Task ApplicationRetirementWaitsForEditorsBeforeDisposingTheFacade()
    {
        var js = new EditorInteropFixture { ReleaseCreation = EditorInteropFixture.NewBarrier() };
        await using var owner = new EditorInteropModule(js);
        var session = js.Create(owner, new EditorCallbackLifetime());
        try
        {
            await js.CreationStarted.Task.WaitAsync(Deadline);
            var disposal = owner.DisposeAsync().AsTask();
            Assert.True(session.IsRetired); Assert.False(disposal.IsCompleted);
            Assert.Equal(0, js.Facade.Disposals);
            Assert.Throws<ObjectDisposedException>(() => js.Create(owner, new EditorCallbackLifetime()));
            js.ReleaseCreation.TrySetResult();
            await disposal.WaitAsync(Deadline);
            Assert.Empty(js.Buffers); Assert.Equal(1, js.Facade.Disposals);
        }
        finally { js.ReleaseCreation.TrySetResult(); }
    }

    [Fact]
    public async Task TheActualComponentRetiresBeforeItsInitializationCompletes()
    {
        var js = new EditorInteropFixture { ReleaseCreation = EditorInteropFixture.NewBarrier() };
        await using var owner = new EditorInteropModule(js);
        var editor = new TestCodeEditor(owner, "source", "Resource.axaml");
        await editor.ParametersAsync(); var render = editor.RenderAsync();
        try
        {
            await js.CreationStarted.Task.WaitAsync(Deadline);
            var disposal = editor.DisposeAsync().AsTask();
            Assert.True(editor.IsRetired); Assert.Null(await editor.TryGetTextAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => editor.GetTextAsync());
            js.ReleaseCreation.TrySetResult();
            await Task.WhenAll(render, disposal).WaitAsync(Deadline);
            await editor.DisposeAsync(); Assert.Empty(js.Buffers);
        }
        finally { js.ReleaseCreation.TrySetResult(); }
    }

    [Fact]
    public async Task ParametersArrivingDuringCreationWinWithoutClobberingLaterUserEdits()
    {
        var js = new EditorInteropFixture { ReleaseCreation = EditorInteropFixture.NewBarrier() };
        await using var owner = new EditorInteropModule(js);
        await using var editor = new TestCodeEditor(owner, "old", "Resource.axaml");
        await editor.ParametersAsync(); var render = editor.RenderAsync();
        try
        {
            await js.CreationStarted.Task.WaitAsync(Deadline);
            var parameters = editor.ParametersAsync(new Dictionary<string, object?> { [nameof(editor.Text)] = "replacement" });
            js.ReleaseCreation.TrySetResult();
            await Task.WhenAll(render, parameters).WaitAsync(Deadline);
            Assert.Equal("replacement", await editor.GetTextAsync());
            js.Buffers[1] = "not yet debounced";
            await editor.ParametersAsync();
            Assert.Equal("not yet debounced", await editor.GetTextAsync());
        }
        finally { js.ReleaseCreation.TrySetResult(); }
    }

    [Fact]
    public async Task AManagedCommandReleasesItsGateBeforeRecursivelyCapturingTheBuffer()
    {
        var js = new EditorInteropFixture(); await using var owner = new EditorInteropModule(js);
        await using var editor = new TestCodeEditor(owner, "source", "Resource.axaml");
        string? captured = null;
        await editor.ParametersAsync(new Dictionary<string, object?>
        {
            [nameof(editor.AuthoringRequested)] = EventCallback.Factory.Create<EditorCommandRequest>(new object(), async _ => captured = await editor.GetTextAsync())
        });
        await editor.RenderAsync();
        await editor.RequestCommandAsync("format").WaitAsync(Deadline);
        Assert.Equal("source", captured);
    }

    [Fact]
    public async Task RetiredCallbacksCannotUpdateTheSuccessorSource()
    {
        var js = new EditorInteropFixture(); await using var owner = new EditorInteropModule(js);
        var editor = new TestCodeEditor(owner); var changes = 0;
        await editor.ParametersAsync(new Dictionary<string, object?>
        { [nameof(editor.TextChanged)] = EventCallback.Factory.Create<string>(new object(), _ => changes++) });
        await editor.DisposeAsync();
        await editor.Changed("stale callback"); await editor.RenderAsync();
        Assert.Equal(0, changes); Assert.Equal(0, js.Imports);
    }
}
