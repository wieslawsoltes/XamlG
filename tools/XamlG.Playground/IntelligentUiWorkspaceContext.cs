using XamlG.IntelligentUI;

namespace XamlG.Playground;

/// <summary>Owner-only Studio composition. No methods on this context are JSInvokable or tools.</summary>
public sealed class IntelligentUiWorkspaceContext
{
    public UiSessionStore? Store { get; private set; }
    public UiCSharpProposals? Proposals { get; private set; }
    private Func<string>? _identity;
    private Func<CancellationToken>? _lifetime;
    private Func<Func<CancellationToken, Task>, CancellationToken, Task>? _exclusive;
    public string? WorkspaceId => _identity?.Invoke();
    public CancellationToken Lifetime => _lifetime?.Invoke() ?? CancellationToken.None;
    public event Action? Changed;
    public void Attach(UiSessionStore store, UiCSharpProposals proposals, Func<string> identity,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> exclusive, Func<CancellationToken>? lifetime = null)
    {
        if (Store != null) Detach(Store);
        Store = store; Proposals = proposals; _identity = identity; _exclusive = exclusive; _lifetime = lifetime;
        store.Changed += OnChanged; store.Released += OnReleased; proposals.Changed += Notify;
        Notify();
    }
    private void OnChanged(UiSnapshot _) => Notify();
    private void OnReleased(string _) => Notify();
    private void Notify() => Changed?.Invoke();
    public Task ExclusiveAsync(Func<CancellationToken, Task> operation, CancellationToken token)
        => _exclusive?.Invoke(operation, token) ?? Task.FromException(new InvalidOperationException("The Studio workspace is not ready."));
    public void Detach(UiSessionStore store)
    {
        if (!ReferenceEquals(Store, store)) return;
        store.Changed -= OnChanged; store.Released -= OnReleased;
        if (Proposals != null) Proposals.Changed -= Notify;
        Store = null; Proposals = null; _identity = null; _exclusive = null; _lifetime = null; Notify();
    }
}
