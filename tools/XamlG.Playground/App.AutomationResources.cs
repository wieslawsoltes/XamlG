using Microsoft.JSInterop;
using XamlG.Automation;
using XamlG.AvaloniaRuntime.Inspection;

namespace XamlG.Playground;

public partial class App
{
    private HashSet<string> _resourceSourcePaths = new(StringComparer.Ordinal);
    private long _resourceSourceRevision = -1;
    private void AddAutomationResources()
    {
        _automation.ResourceChanged += uri => { _ = PublishResourceChangeAsync(uri); };
        Resource("xamlg://runtime", "Live Avalonia visual and logical trees", "xamlg_runtime_tree");
        Resource("xamlg://runtime/accessibility", "Live Avalonia accessibility peers", "xamlg_runtime_accessibility");
        _automation.AddResourceTemplate(new("xamlg://source/{path}", "Source document", "Current source with UTF-16 range and revision metadata."),
            async (values, context) => (await _automation.CallAsync("xamlg_document_read", AutomationJson.Element(new { path = values["path"], offset = 0, count = 262144 }), context)).GetRawText(),
            async (argument, value, context) =>
            {
                var project = await _automation.CallAsync("xamlg_project_get", AutomationJson.Element(new { }), context);
                var paths = argument == "path" ? project.GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("path").GetString()!) : [];
                return CompletePaths(paths, value);
            });
        _automation.AddResourceTemplate(new("xamlg://generated/{path}", "Generated C# document", "Generated C# with source revision and bounded text."),
            async (values, context) => (await _automation.CallAsync("xamlg_generated_read", AutomationJson.Element(new { path = values["path"], offset = 0, count = 262144 }), context)).GetRawText(),
            async (argument, value, context) =>
            {
                var generated = await _automation.CallAsync("xamlg_generated_list", AutomationJson.Element(new { }), context);
                var paths = argument == "path" ? generated.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("path").GetString()!) : [];
                return CompletePaths(paths, value);
            });
        _automation.AddResourceTemplate(new("xamlg://runtime/{objectId}/properties", "Live object properties", "Registered, attached and CLR properties of a live Avalonia handle."),
            async (values, context) => (await _automation.CallAsync("xamlg_runtime_properties", AutomationJson.Element(new { objectId = values["objectId"] }), context)).GetRawText(),
            async (argument, value, context) =>
            {
                var tree = await _automation.CallAsync("xamlg_runtime_tree", AutomationJson.Element(new { }), context);
                var ids = argument == "objectId" ? tree.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("id").GetString()!) : [];
                return CompletePaths(ids, value);
            });
        _resourceSourcePaths = WorkspaceTexts().Keys.ToHashSet(StringComparer.Ordinal);
        _resourceSourceRevision = SourceRevision;
    }
    private static AutomationCompletion CompletePaths(IEnumerable<string> paths, string value)
    {
        var matches = paths.Where(path => path.StartsWith(value, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        return new(matches.Take(100).ToArray(), matches.Length, matches.Length > 100);
    }
    private void NotifySourceResources()
    {
        if (_automation == null) return;
        foreach (var uri in new[] { "xamlg://project", "xamlg://diagnostics", "xamlg://generated", "xamlg://designer", "xamlg://compiler/options" }) _automation.NotifyResourceChanged(uri);
        var paths = WorkspaceTexts().Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var path in paths.Union(_resourceSourcePaths)) _automation.NotifyResourceChanged("xamlg://source/" + Uri.EscapeDataString(path));
        _resourceSourcePaths = paths;
        _resourceSourceRevision = SourceRevision;
        NotifyCompilerResources();
    }
    private void NotifyCompilerResources()
    {
        if (_automation == null) return;
        _automation.NotifyResourceChanged("xamlg://diagnostics"); _automation.NotifyResourceChanged("xamlg://generated");
        if (_result != null)
            foreach (var file in _result.Compilation.SyntaxTrees.Where(tree => !_result.SourcePaths.Contains(tree.FilePath)))
                _automation.NotifyResourceChanged("xamlg://generated/" + Uri.EscapeDataString(file.FilePath));
    }
    private void NotifyRuntimeResource(RuntimeChange change)
    {
        _automation.NotifyResourceChanged("xamlg://runtime");
        _automation.NotifyResourceChanged("xamlg://designer");
        if (change.Kind == "accessibility") _automation.NotifyResourceChanged("xamlg://runtime/accessibility");
        _automation.NotifyResourceChanged("xamlg://runtime/" + Uri.EscapeDataString(change.ObjectId) + "/properties");
    }
    private async Task PublishResourceChangeAsync(string uri)
    {
        if (_module == null || !_sharing) return;
        try { await _module.InvokeVoidAsync("notifyAutomationResource", uri); }
        catch (Exception error) when (error is JSException or ObjectDisposedException) { }
    }
}
