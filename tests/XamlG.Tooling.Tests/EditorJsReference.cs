using Microsoft.JSInterop;

namespace XamlG.Tooling.Tests;

internal sealed class EditorJsReference(Func<string, object?[]?, Task<object?>> invoke) : IJSObjectReference
{
    public int Disposals { get; private set; }
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        ObjectDisposedException.ThrowIf(Disposals != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await invoke(identifier, args);
        ObjectDisposedException.ThrowIf(Disposals != 0, this);
        return value is null ? default! : (TValue)value;
    }
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}
