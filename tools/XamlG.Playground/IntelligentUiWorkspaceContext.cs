using XamlG.IntelligentUI;

namespace XamlG.Playground;

/// <summary>Owner-only Studio composition. No methods on this context are JSInvokable or tools.</summary>
public sealed class IntelligentUiWorkspaceContext
{
    public UiSessionStore? Store { get; private set; }
    private Func<string>? _identity;
    private Func<Func<CancellationToken, Task>, CancellationToken, Task>? _exclusive;
    public string? WorkspaceId => _identity?.Invoke();
    public event Action? Changed;
    public void Attach(UiSessionStore store, Func<string> identity, Func<Func<CancellationToken, Task>, CancellationToken, Task> exclusive)
    { Store = store; _identity = identity; _exclusive = exclusive; Changed?.Invoke(); }
    public Task ExclusiveAsync(Func<CancellationToken, Task> operation, CancellationToken token)
        => _exclusive?.Invoke(operation, token) ?? Task.FromException(new InvalidOperationException("The Studio workspace is not ready."));
    public void Detach(UiSessionStore store)
    { if (ReferenceEquals(Store, store)) { Store = null; _identity = null; _exclusive = null; Changed?.Invoke(); } }
}
