using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Reusable native focus, blur and Enter semantics over inert form annotations.</summary>
public sealed class UiAvaloniaFormBehavior : IDisposable
{
    private readonly UiAvaloniaRenderer _renderer;
    private UiSnapshot? _snapshot;
    private bool _applying, _disposed;
    public event Action<UiFormCall>? TouchRequested;
    public event Action<UiFormCall>? SubmitRequested;

    public UiAvaloniaFormBehavior(UiAvaloniaRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer); Dispatcher.UIThread.VerifyAccess(); _renderer = renderer;
        renderer.View.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        renderer.View.AddHandler(InputElement.LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
    }
    public void Apply(UiSnapshot snapshot)
    {
        Dispatcher.UIThread.VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        _applying = true;
        try { _renderer.Apply(snapshot); _snapshot = snapshot; }
        finally { _applying = false; }
    }
    private UiElement? Input(object? source)
    {
        if (_snapshot == null || source is not Visual visual) return null;
        var nodes = UiSessionStore.Flatten(_snapshot.Roots).Where(node => node.Form?.Role == "input").ToArray();
        foreach (var ancestor in visual.GetVisualAncestors().Prepend(visual))
        {
            var node = nodes.FirstOrDefault(candidate => ReferenceEquals(_renderer.Find(candidate.Key), ancestor));
            if (node != null) return node;
        }
        return null;
    }
    private void OnLostFocus(object? sender, RoutedEventArgs args)
    {
        if (_disposed || _applying || _snapshot == null || Input(args.Source) is not { Form: { } form } node) return;
        TouchRequested?.Invoke(new(_snapshot.Id, _snapshot.Revision, _snapshot.StateRevision, form.Id, node.Key));
    }
    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (_disposed || _applying || _snapshot == null || args.Handled || args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None ||
            Input(args.Source) is not { Form: { } form } node) return;
        var control = _renderer.Find(node.Key);
        // Enter belongs to multiline editing and open selection popups, not form submission.
        if (control is TextBox { AcceptsReturn: true } or ComboBox { IsDropDownOpen: true }) return;
        args.Handled = true;
        SubmitRequested?.Invoke(new(_snapshot.Id, _snapshot.Revision, _snapshot.StateRevision, form.Id));
    }
    public void Focus(string? key)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (key == null || _snapshot == null || _disposed) return;
        var snapshot = _snapshot;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(snapshot, _snapshot)) return;
            if (_renderer.Find(key) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } control)
            { control.BringIntoView(); control.Focus(NavigationMethod.Tab); }
        });
    }
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess(); if (_disposed) return; _disposed = true;
        _renderer.View.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _renderer.View.RemoveHandler(InputElement.LostFocusEvent, OnLostFocus);
        _snapshot = null; TouchRequested = null; SubmitRequested = null;
    }
}
