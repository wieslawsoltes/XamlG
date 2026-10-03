using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Threading;
using XamlG.AvaloniaRuntime;
using XamlG.Runtime;

namespace XamlG.Playground;

public sealed class AvaloniaPreviewHost : IDisposable
{
    private AvaloniaView? _view;
    private Control? _root;
    private XamlRuntimeSession? _session;
    public Control? Root => _root;

    public async Task InitializeAsync(string elementId, Uri baseUri)
    {
        if (_view != null) return;
        await AppBuilder.Configure<PreviewApplication>().WithInterFont().SetupBrowserAppAsync(new BrowserPlatformOptions
        {
            FrameworkAssetPathResolver = file => new Uri(baseUri, "_content/Avalonia.Browser/" + file).AbsoluteUri,
            RegisterAvaloniaServiceWorker = false
        });
        _view = new AvaloniaView(elementId);
    }

    public async Task<AvaloniaVisualNode> ShowAsync(object root)
    {
        if (_view == null) throw new InvalidOperationException("The Avalonia preview host is not ready.");
        if (root is not Control control) throw new InvalidOperationException("The preview root must derive from Avalonia.Controls.Control.");
        _view.Content = control;
        _root = control;
        _session?.Dispose();
        XamlRuntimeSession.TryGet(control, out _session);
        await Dispatcher.UIThread.InvokeAsync(() => control.UpdateLayout(), DispatcherPriority.Loaded);
        return AvaloniaVisualInspector.Inspect(control);
    }

    public AvaloniaVisualNode? Inspect() => _root == null ? null : AvaloniaVisualInspector.Inspect(_root);
    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        if (_view != null) _view.Content = null;
        _root = null;
    }
}
