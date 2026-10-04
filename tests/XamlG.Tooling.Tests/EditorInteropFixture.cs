using System.Collections.Concurrent;
using Microsoft.JSInterop;
using XamlG.Playground;
using XamlG.Playground.Editing;

namespace XamlG.Tooling.Tests;

/// <summary>Explicit barriers replace sleeps and reproduce the real JS reference disposal contract.</summary>
internal sealed class EditorInteropFixture : IJSRuntime
{
    private int _sequence;
    public EditorInteropFixture()
    {
        Facade = new(DispatchAsync);
        Source = new((method, _) => method == "createEditorInterop" ? Task.FromResult<object?>(Facade) : throw new InvalidOperationException(method));
    }
    public EditorJsReference Source { get; }
    public EditorJsReference Facade { get; }
    public int Imports { get; private set; }
    public ConcurrentDictionary<int, string> Buffers { get; } = new();
    public ConcurrentQueue<string> Events { get; } = new();
    public TaskCompletionSource CreationStarted { get; } = NewBarrier();
    public TaskCompletionSource ReadStarted { get; } = NewBarrier();
    public TaskCompletionSource? ReleaseCreation { get; set; }
    public TaskCompletionSource? ReleaseRead { get; set; }
    public bool FailCreation { get; set; }
    public bool FailRead { get; set; }
    public bool FailCleanup { get; set; }
    public static TaskCompletionSource NewBarrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EditorInteropSession Create(EditorInteropModule owner, EditorCallbackLifetime callback, string text = "initial") =>
        owner.Create(new object?[] { null, callback, text, "xml", false, "Resource.axaml" }, callback);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (identifier != "import") throw new InvalidOperationException(identifier);
        Imports++;
        return ValueTask.FromResult((TValue)(object)Source);
    }

    private async Task<object?> DispatchAsync(string method, object?[]? args)
    {
        if (method == "createEditor")
        {
            CreationStarted.TrySetResult();
            if (ReleaseCreation != null) await ReleaseCreation.Task;
            if (FailCreation) throw new InvalidOperationException("Creation failed.");
            var id = Interlocked.Increment(ref _sequence);
            Buffers[id] = (string)args![2]!;
            Events.Enqueue("create:" + id);
            return id;
        }
        var identity = (int)args![0]!;
        Events.Enqueue(method + ":" + identity);
        switch (method)
        {
            case "getEditorText":
                ReadStarted.TrySetResult();
                if (ReleaseRead != null) await ReleaseRead.Task;
                if (FailRead) throw new InvalidOperationException("Live read failed.");
                return Buffers[identity];
            case "setEditorText": Buffers[identity] = (string)args[1]!; return null;
            case "disposeEditor":
                if (!Buffers.TryRemove(identity, out _)) throw new InvalidOperationException("Editor was disposed twice.");
                if (FailCleanup) throw new InvalidOperationException("Cleanup failed.");
                return null;
            case "getAuthoringRequest": return new EditorCommandRequest((string)args[1]!, "Resource.axaml", Buffers[identity], 0, 0);
            case "reveal": case "setMarkers": return null;
            default: throw new InvalidOperationException(method);
        }
    }
}
