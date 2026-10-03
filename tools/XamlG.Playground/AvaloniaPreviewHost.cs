using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Threading;
using XamlG.AvaloniaRuntime;
using XamlG.AvaloniaRuntime.Design;
using XamlG.Runtime;
using XamlG.Runtime.Design;
using XamlG.Runtime.Reload;

namespace XamlG.Playground;

public sealed class AvaloniaPreviewHost : IDisposable
{
    private AvaloniaView? _view;
    private AvaloniaDesignerSurface? _surface;
    private XamlReloadSession? _reload;
    public Func<XamlSourceInfo, Task>? SourceSelected { get; set; }
    public Func<XamlVisualEdit, Task>? EditRequested { get; set; }
    public Action<Exception>? Error { get; set; }
    public Control? Root => _reload?.Root as Control;
    public long Revision => _reload?.Revision ?? 0;
    public bool IsDesignMode { get => _surface?.IsDesignMode == true; set { if (_surface != null) _surface.IsDesignMode = value; } }
    public IReadOnlyList<string> LastWarnings { get; private set; } = Array.Empty<string>();

    public async Task InitializeAsync(string elementId, Uri baseUri)
    {
        if (_view != null) return;
        await AppBuilder.Configure<PreviewApplication>().WithInterFont().SetupBrowserAppAsync(new BrowserPlatformOptions
        {
            FrameworkAssetPathResolver = file => new Uri(baseUri, "_content/Avalonia.Browser/" + file).AbsoluteUri,
            RegisterAvaloniaServiceWorker = false
        });
        _view = new AvaloniaView(elementId);
        _surface = new AvaloniaDesignerSurface();
        _surface.SourceSelected += OnSourceSelected;
        _surface.EditRequested += OnEditRequested;
        _view.Content = _surface;
    }
    public async Task<AvaloniaVisualNode> ShowAsync(object root)
    {
        if (_view == null || _surface == null) throw new InvalidOperationException("The Avalonia preview host is not ready.");
        if (root is not Control control) throw new InvalidOperationException("The preview root must derive from Avalonia.Controls.Control.");
        if (_reload == null)
        {
            _surface.Content = control;
            _reload = new(control, new[] { new AvaloniaInteractiveStateAdapter() });
        }
        else
        {
            var result = _reload.Reload(_reload.Revision, () => control, candidate => _surface.Content = (Control)candidate);
            LastWarnings = result.Warnings;
            if (!result.Applied) throw new InvalidOperationException(result.Error);
        }
        await Dispatcher.UIThread.InvokeAsync(() => control.UpdateLayout(), DispatcherPriority.Loaded);
        return AvaloniaVisualInspector.Inspect(control);
    }
    public void SelectSource(int start) => _surface?.SelectSource(start);
    public AvaloniaVisualNode? Inspect() => Root is { } root ? AvaloniaVisualInspector.Inspect(root) : null;
    private async void OnSourceSelected(XamlSourceInfo source)
    {
        try { if (SourceSelected != null) await SourceSelected(source); }
        catch (Exception error) { Error?.Invoke(error); }
    }
    private async void OnEditRequested(XamlVisualEdit edit)
    {
        try { if (EditRequested != null) await EditRequested(edit); }
        catch (Exception error) { Error?.Invoke(error); }
    }
    public void Dispose()
    {
        SourceSelected = null; EditRequested = null; Error = null;
        if (_surface != null)
        {
            _surface.SourceSelected -= OnSourceSelected; _surface.EditRequested -= OnEditRequested;
            _surface.Content = null;
        }
        if (Root is { } root && XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose();
        _reload = null;
        if (_view != null) _view.Content = null;
    }
}
