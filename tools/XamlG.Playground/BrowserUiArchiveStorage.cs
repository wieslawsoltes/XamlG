using Microsoft.JSInterop;
using XamlG.IntelligentUI;

namespace XamlG.Playground;

/// <summary>IndexedDB adapter. The JavaScript transaction is the cross-tab compare-and-swap boundary.</summary>
public sealed class BrowserUiArchiveStorage(IJSRuntime javaScript) : IUiArchiveStorage, IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;
    private bool _disposed;
    private Task<IJSObjectReference> Module()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _module ??= javaScript.InvokeAsync<IJSObjectReference>("import", "./ui-archive-storage.js").AsTask();
    }
    public async ValueTask<UiStoredArchive?> ReadAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var module = await Module();
        return await module.InvokeAsync<UiStoredArchive?>("read", cancellationToken, workspaceId);
    }
    public async ValueTask<string> WriteAsync(string workspaceId, string json, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        var module = await Module();
        try { return await module.InvokeAsync<string>("write", cancellationToken, workspaceId, json, expectedVersion); }
        catch (JSException error) when (error.Message.Contains("storage_conflict", StringComparison.Ordinal))
        { throw new UiException("storage_conflict", "Another tab changed this UI workspace. Reload it before saving."); }
    }
    public async ValueTask DeleteAsync(string workspaceId, string expectedVersion, CancellationToken cancellationToken = default)
    {
        var module = await Module();
        try { await module.InvokeVoidAsync("forget", cancellationToken, workspaceId, expectedVersion); }
        catch (JSException error) when (error.Message.Contains("storage_conflict", StringComparison.Ordinal))
        { throw new UiException("storage_conflict", "Another tab changed this UI workspace. Reload it before forgetting."); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        if (_module is { IsCompletedSuccessfully: true }) await _module.Result.DisposeAsync();
    }
}
