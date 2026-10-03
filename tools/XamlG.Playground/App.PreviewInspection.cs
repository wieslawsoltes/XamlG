namespace XamlG.Playground;

public partial class App
{
    private async Task RefreshCurrentVisualsAsync()
    {
        try
        {
            _visualTree = _isolationVisible && _isolatedPreview != null
                ? (await _isolatedPreview.InspectAsync()).Tree
                : Preview.Inspect();
        }
        catch (Exception error) { Report(error); }
    }
}
