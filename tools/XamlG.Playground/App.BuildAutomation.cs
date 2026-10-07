using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.JSInterop;
using XamlG.Automation;

namespace XamlG.Playground;

public partial class App
{
    private readonly AutomationArtifactStore _buildArtifacts = new();
    private readonly AutomationActivityLog _automationActivity = new();
    private string _capabilityFilter = "", _capabilityScope = "", _activityFilter = "";
    private bool _activityErrorsOnly, _activityFollow = true;
    private IReadOnlyList<AutomationActivity> _frozenActivity = [];
    private IEnumerable<AutomationTool> FilteredTools => _automation.Tools.Where(tool =>
        (_capabilityScope.Length == 0 || tool.Scope.ToString() == _capabilityScope) &&
        (tool.Name.Contains(_capabilityFilter, StringComparison.OrdinalIgnoreCase) || tool.Description.Contains(_capabilityFilter, StringComparison.OrdinalIgnoreCase)));
    private IEnumerable<AutomationActivity> FilteredActivity => (_activityFollow ? _automationActivity.Snapshot : _frozenActivity).Reverse().Where(item =>
        (!_activityErrorsOnly || item.Status is "failed" or "cancelled") &&
        (item.Name.Contains(_activityFilter, StringComparison.OrdinalIgnoreCase) || item.Caller.Contains(_activityFilter, StringComparison.OrdinalIgnoreCase) ||
         item.Status.Contains(_activityFilter, StringComparison.OrdinalIgnoreCase)));
    private void SetActivityFollow(Microsoft.AspNetCore.Components.ChangeEventArgs args)
    { _frozenActivity = _automationActivity.Snapshot; _activityFollow = args.Value is true; }
    private void ClearAutomationActivity() { _automationActivity.Clear(); _frozenActivity = []; }
    private async Task ExportAutomationActivityAsync()
    {
        if (_module != null) await _module.InvokeVoidAsync("download", "xamlg-automation-activity.json", JsonSerializer.Serialize(FilteredActivity.ToArray()), "application/json");
    }
    private void AddBuildAutomation()
    {
        _automation.Add<WaitArguments, object>("xamlg_wait", "Wait for source revisions or resource changes, up to 110 seconds, without blocking other IDE operations. Modern MCP task clients can poll or cancel this wait.", AutomationScope.Agent, AutomationEffect.Read,
            async (args, context) =>
            {
                if (args.Milliseconds is < 1 or > 110000) throw new ArgumentOutOfRangeException(nameof(args.Milliseconds));
                if (args.AfterRevision is < 0 || args.AfterRevision > SourceRevision) throw new ArgumentOutOfRangeException(nameof(args.AfterRevision));
                var resources = args.Resources ?? ["xamlg://project", "xamlg://runtime", "xamlg://diagnostics"];
                if (resources.Length > 128 || resources.Any(uri => string.IsNullOrEmpty(uri) || uri.Length > 4096 ||
                    !_automation.Resources.Any(resource => AutomationUriTemplate.IsMatch(resource, uri))))
                    throw new ArgumentException("Supply at most 128 supported resource URIs.");
                var updated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(string uri) { if (resources.Contains(uri, StringComparer.Ordinal)) updated.TrySetResult(uri); }
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                string? changedResource = null;
                _automation.ResourceChanged += Changed;
                try
                {
                    if (args.AfterRevision is { } revision && SourceRevision > revision) changedResource = "xamlg://project";
                    else
                        try { changedResource = await updated.Task.WaitAsync(TimeSpan.FromMilliseconds(args.Milliseconds), context.CancellationToken); }
                        catch (TimeoutException) { }
                    context.CancellationToken.ThrowIfCancellationRequested();
                    return new { reason = changedResource == null ? "timeout" : "changed", changedResource,
                        elapsedMilliseconds = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        revision = SourceRevision, ready = _ready && !_busy };
                }
                finally { _automation.ResourceChanged -= Changed; }
            });
        _buildArtifacts.Changed += id => _automation.NotifyResourceChanged("xamlg://artifacts/" + id);
        AddAutomation<NoArguments>("build_targets", "List inert build/export formats supported by the existing browser compiler.", AutomationScope.Build, AutomationEffect.Read,
            (_, _) => new { targets = new[] { "project-json", "source-zip", "assembly", "symbols" }, artifactTtlSeconds = 300, maxArtifacts = 8, maxTotalBytes = 33554432, maxReadBytes = 262144 });
        AddAutomation<BuildArguments>("build_create", "Create an immutable, principal-bound project JSON, source ZIP, managed assembly or portable PDB without executing application code.", AutomationScope.Build, AutomationEffect.Read,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision); context.CancellationToken.ThrowIfCancellationRequested();
                byte[] bytes; string name, mime;
                if (args.Target == "project-json")
                {
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new { format = "xamlg-project", version = 3, xaml = _document.Current.Text,
                        code = _code, resources = ResourceTexts(), codeFiles = CodeTexts() }); name = "xamlg-project.json"; mime = "application/json";
                }
                else if (args.Target == "source-zip")
                {
                    var compilation = AnalyzeAutomation(context.CancellationToken);
                    using var output = new MemoryStream();
                    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                    {
                        foreach (var source in WorkspaceTexts()) Write("source/" + source.Key, source.Value);
                        foreach (var tree in compilation.Compilation.SyntaxTrees.Where(tree => !compilation.SourcePaths.Contains(tree.FilePath)))
                            Write("generated/" + tree.FilePath.Replace('\\', '/').TrimStart('/'), tree.GetText(context.CancellationToken).ToString());
                        Write("diagnostics.json", JsonSerializer.Serialize(compilation.Diagnostics));
                        void Write(string path, string content)
                        {
                            var normalized = path.Replace('\\', '/');
                            if (normalized.Length > 1024 || normalized.StartsWith('/') || normalized.Contains(':') ||
                                normalized.Any(char.IsControl) || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
                                throw new InvalidOperationException("A source path cannot be represented safely in the ZIP export.");
                            using var writer = new StreamWriter(zip.CreateEntry(normalized).Open(), new UTF8Encoding(false)); writer.Write(content);
                        }
                    }
                    bytes = output.ToArray(); name = "xamlg-source.zip"; mime = "application/zip";
                }
                else if (args.Target is "assembly" or "symbols")
                {
                    var compilation = AnalyzeAutomation(context.CancellationToken);
                    if (!compilation.Success) throw new InvalidOperationException("Resolve compilation errors before emitting an assembly or symbols.");
                    using var image = new MemoryStream(); using var symbols = new MemoryStream();
                    var emitCompilation = compilation.Compilation;
                    if (args.Target == "symbols")
                        emitCompilation = emitCompilation.RemoveAllSyntaxTrees().AddSyntaxTrees(compilation.Compilation.SyntaxTrees.Select(tree =>
                            tree.WithChangedText(SourceText.From(tree.GetText(context.CancellationToken).ToString(), Encoding.UTF8))));
                    var emitted = args.Target == "symbols" ? emitCompilation.Emit(image, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb), cancellationToken: context.CancellationToken) :
                        emitCompilation.Emit(image, cancellationToken: context.CancellationToken);
                    if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(50)));
                    bytes = args.Target == "symbols" ? symbols.ToArray() : image.ToArray();
                    name = args.Target == "symbols" ? "XamlG.Preview.pdb" : "XamlG.Preview.dll"; mime = "application/octet-stream";
                }
                else throw new ArgumentException("Unknown build target.");
                return _buildArtifacts.Add(context.PrincipalId ?? context.Caller, name, mime, SourceRevision, bytes);
            });
        AddAutomation<ArtifactReadArguments>("build_read", "Read a bounded base64 byte chunk with immutable SHA-256 and source revision metadata.", AutomationScope.Build, AutomationEffect.Read,
            (args, context) => _buildArtifacts.Read(args.Id, context.PrincipalId ?? context.Caller, args.Offset, args.Count));
        AddAutomation<ArtifactArguments>("build_release", "Release this principal's build artifact before its expiry.", AutomationScope.Build, AutomationEffect.Read,
            (args, context) => { _buildArtifacts.Release(args.Id, context.PrincipalId ?? context.Caller); return new { released = true }; });
        _automation.AddResourceTemplate(new("xamlg://artifacts/{artifactId}", "Build artifact", "First bounded artifact chunk; use build_read for subsequent byte ranges."),
            async (values, context) => (await _automation.CallAsync("xamlg_build_read", AutomationJson.Element(new { id = values["artifactId"] }), context)).GetRawText());
    }
    private async Task DownloadArtifactAsync(AutomationArtifact artifact)
    {
        if (_module == null) return;
        try { await _module.InvokeVoidAsync("downloadBytes", artifact.Name, _buildArtifacts.ReadLocal(artifact.Id), artifact.MimeType); }
        catch (Exception error) { Report(error); }
    }
    private async Task ExportAutomationCatalogAsync()
    {
        if (_module != null) await _module.InvokeVoidAsync("download", "xamlg-automation-catalog.json", AutomationCatalog().GetRawText(), "application/json");
    }
    public sealed record BuildArguments(string Target, long ExpectedRevision);
    public sealed record WaitArguments(int Milliseconds = 30000, long? AfterRevision = null, string[]? Resources = null);
    public sealed record ArtifactArguments(string Id);
    public sealed record ArtifactReadArguments(string Id, int Offset = 0, int Count = 262144);
}
