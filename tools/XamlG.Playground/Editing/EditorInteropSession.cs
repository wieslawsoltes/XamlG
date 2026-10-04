using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace XamlG.Playground.Editing;

/// <summary>Owns exactly one JS editor and callback reference. Retirement is synchronous;
/// initialization and admitted buffer operations finish before cleanup releases either resource.</summary>
public sealed class EditorInteropSession : IAsyncDisposable
{
    private readonly Task<EditorJsHandle> _creation;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly IDisposable _callback;
    private readonly Action<EditorInteropSession> _released;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _retired;

    internal EditorInteropSession(Task<IJSObjectReference> module, object?[] arguments,
        IDisposable callback, Action<EditorInteropSession> released)
    {
        _callback = callback;
        _released = released;
        _creation = CreateAsync(module, arguments);
    }

    public bool IsRetired => Volatile.Read(ref _retired) != 0;
    public Task Ready => _creation;

    private static async Task<EditorJsHandle> CreateAsync(Task<IJSObjectReference> module, object?[] arguments)
    {
        var reference = await module;
        var id = await reference.InvokeAsync<int>("createEditor", arguments);
        if (id <= 0) throw new InvalidOperationException("JavaScript returned an invalid editor identity.");
        return new(reference, id);
    }

    /// <summary>Returns null only when this editor has retired. Failures of a live editor propagate.
    /// Functions invoked here must return data, not await a callback into the managed editor.</summary>
    public async Task<T?> ReadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] T>(
        string identifier, params object?[] arguments) where T : class
    {
        if (IsRetired) return null;
        var handle = await _creation;
        await _operations.WaitAsync();
        try
        {
            if (IsRetired) return null;
            var result = await handle.Module.InvokeAsync<T>(identifier, WithId(handle.Id, arguments));
            // Cleanup cannot overtake the call, but its result must not escape after retirement.
            return IsRetired ? null : result;
        }
        finally { _operations.Release(); }
    }

    public async Task InvokeAsync(string identifier, params object?[] arguments)
    {
        if (IsRetired) return;
        var handle = await _creation;
        await _operations.WaitAsync();
        try
        {
            if (!IsRetired) await handle.Module.InvokeVoidAsync(identifier, WithId(handle.Id, arguments));
        }
        finally { _operations.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0) _ = ReleaseAsync();
        return new(_completion.Task);
    }

    private async Task ReleaseAsync()
    {
        Exception? failure = null;
        try
        {
            EditorJsHandle? handle;
            try { handle = await _creation; }
            catch { handle = null; } // The initialization caller observes creation errors.
            if (handle != null)
            {
                await _operations.WaitAsync();
                try { await handle.Module.InvokeVoidAsync("disposeEditor", handle.Id); }
                catch (JSDisconnectedException) { }
                finally { _operations.Release(); }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { _callback.Dispose(); }
            catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
            finally { _released(this); }
        }
        if (failure == null) _completion.TrySetResult();
        else _completion.TrySetException(failure);
        // Do not dispose the semaphore: already queued readers may still wake to observe retirement.
        // It never exposes AvailableWaitHandle and owns no unmanaged wait handle.
    }

    private static object?[] WithId(int id, object?[] arguments)
    {
        var result = new object?[arguments.Length + 1];
        result[0] = id;
        Array.Copy(arguments, 0, result, 1, arguments.Length);
        return result;
    }
}
