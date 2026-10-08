using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private static readonly Dictionary<string, ThreadState> ThreadStates = new(StringComparer.Ordinal);
    private ThreadState Thread => ThreadStates.TryGetValue(_selectedId, out var state) ? state : ThreadStates[_selectedId] = new();
    private EventView[] VisibleEvents => Thread.Page?.Events ?? Selected?.Events ?? [];
    private string VisibleLiveText => Thread.Page == null ? _liveText : Thread.Page.LiveText ?? "";
    private bool ThreadHasEarlier => Thread.Page?.HasEarlier ?? Selected?.PublicEventCount > VisibleEvents.Length;
    private bool ThreadHasLater => Thread.Page is { } page && (page.HasLater || (Selected?.Events.LastOrDefault()?.Sequence ?? 0) > (page.Events.LastOrDefault()?.Sequence ?? 0));
    [JSInvokable] public void AgentThreadFollowing(string id, bool following)
    {
        if (_disposed || id != _selectedId || Selected == null) return;
        if (following) { Thread.Version++; Thread.Page = null; }
        else if (Thread.Page == null) Thread.Page = new() { Events = Selected.Events, HasEarlier = ThreadHasEarlier, LiveText = _liveText };
        StateHasChanged();
    }
    private void PruneThreadState()
    { foreach (var id in ThreadStates.Keys.Where(id => !_state.Tasks.Any(task => task.Id == id)).ToArray()) ThreadStates.Remove(id); }
    private async Task PageThreadAsync(bool earlier)
    {
        var view = Thread; if (view.Busy || VisibleEvents.Length == 0) return;
        var id = _selectedId; var version = ++view.Version; view.Busy = true;
        try
        {
            var page = await RequestAsync<ThreadPage>("thread", new { id,
                beforeSequence = earlier ? (long?)VisibleEvents[0].Sequence : null,
                afterSequence = earlier ? null : (long?)VisibleEvents[^1].Sequence });
            if (view.Version == version)
            {
                if (page.Events.Length != 0) view.Page = page;
                else _error = "Older thread entries have expired from the bounded history. Follow latest to continue.";
                if (_selectedId == id) await _module!.InvokeVoidAsync("holdAgentThread", _threadElement);
            }
        }
        catch (JSException error) { _error = error.Message; }
        finally { view.Busy = false; }
    }
    private async Task FollowThreadAsync()
    {
        Thread.Version++; Thread.Page = null;
        await _module!.InvokeVoidAsync("followAgentThread", _threadElement);
    }
    private sealed class ThreadState { public ThreadPage? Page; public bool Busy; public int Version; }
    public sealed class ThreadPage { public EventView[] Events { get; set; } = []; public bool HasEarlier { get; set; } public bool HasLater { get; set; } public string? LiveText { get; set; } }
}
