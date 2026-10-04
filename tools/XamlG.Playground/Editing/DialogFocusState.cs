namespace XamlG.Playground.Editing;

/// <summary>Requests focus once on opening and after a busy render disables the focused
/// control. Ordinary text/preview renders must not steal focus from another dialog control.</summary>
public sealed class DialogFocusState
{
    private bool _visible;
    private bool _busy;

    public bool Observe(bool visible, bool busy)
    {
        var focus = visible && !busy && (!_visible || _busy);
        _visible = visible;
        _busy = busy;
        return focus;
    }
}
