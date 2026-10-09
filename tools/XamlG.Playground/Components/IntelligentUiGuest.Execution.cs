using System.Text.Json;
using Microsoft.JSInterop;
using XamlG.Automation;
using XamlG.IntelligentUI;

namespace XamlG.Playground.Components;

public partial class IntelligentUiGuest
{
    private IJSObjectReference? _executionModule, _executionWorker;
    private readonly SemaphoreSlim _executionCalls = new(1);
    private bool _executed;

    private async ValueTask<T> ExecuteCommandAsync<T>(string method, object arguments)
    {
        await _executionCalls.WaitAsync(_lifetime.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var worker = _executionWorker ?? throw new UiException("execution_unavailable", "The approved worker is unavailable. Review a fresh execution.");
            var json = JsonSerializer.Serialize(arguments, AutomationJson.Options);
            var result = await worker.InvokeAsync<string>("call", _lifetime.Token, method, json);
            if (result.Length > 2097152) throw new UiException("execution_limit", "Worker result exceeds its bound.");
            return JsonSerializer.Deserialize<T>(result, AutomationJson.Options)
                ?? throw new UiException("execution_result", "Invalid worker result.");
        }
        finally { _executionCalls.Release(); }
    }

    [JSInvokable]
    public async Task<object> ExecuteApproved(UiPublish request)
    {
        if (Mode != "execution" || _executed || _disposed) throw new UiException("execution_not_approved", "Create a fresh reviewed execution frame.");
        request = UiCSharpDeclaration.Normalize(request);
        _executed = true;
        var assetBase = new Uri(Navigation.BaseUri);
        _executionModule = await JavaScript.InvokeAsync<IJSObjectReference>("import", _lifetime.Token,
            new Uri(assetBase, "ui-execution-worker-client.js").AbsoluteUri);
        var candidate = await _executionModule.InvokeAsync<IJSObjectReference>("create", _lifetime.Token, assetBase.AbsoluteUri);
        if (_disposed) { try { await candidate.InvokeVoidAsync("dispose"); } finally { await candidate.DisposeAsync(); } throw new OperationCanceledException(); }
        _executionWorker = candidate;
        var snapshot = await ExecuteCommandAsync<UiSnapshot>("publish", request);
        await ReceiveSnapshot(snapshot);
        return new { snapshot.Id, snapshot.Revision, snapshot.StateRevision, snapshot.FallbackMarkdown,
            expressionLanguage = "csharp-full", execution = "dedicated-worker", wasmMemoryBytes = 536870912,
            compilationMilliseconds = 20000, interactionMilliseconds = 3000, network = false };
    }

    private async ValueTask DisposeExecutionAsync()
    {
        var worker = _executionWorker; _executionWorker = null;
        if (worker != null)
        {
            try { await worker.InvokeVoidAsync("dispose"); } catch (JSDisconnectedException) { }
            await worker.DisposeAsync();
        }
        if (_executionModule != null) { await _executionModule.DisposeAsync(); _executionModule = null; }
    }
}
