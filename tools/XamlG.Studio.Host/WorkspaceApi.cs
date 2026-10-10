using System.ComponentModel;
using System.Text.Json;
using XamlG.Automation;
using XamlG.ProjectSystem;
using XamlG.Workspaces.Studio;

namespace XamlG.Studio.Host;

/// <summary>
/// Routed exclusively by the existing /agent owner endpoint. These actions are not registered as
/// agent/MCP tools, and possession of the lower-privilege MCP token does not authorize local I/O.
/// </summary>
internal sealed class WorkspaceApi
{
    private readonly Lazy<StudioWorkspaceService?> _workspace = new(() =>
        Environment.GetEnvironmentVariable("XAMLG_STUDIO_WORKSPACE_ROOT") is { Length: > 0 } root ? new(root) : null);

    public async ValueTask<JsonElement> ExecuteAsync(string action, JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            var service = _workspace.Value;
            if (action == "workspace_status") return AutomationJson.Element(new
            {
                enabled = service != null,
                root = service?.Files.Root,
                backend = "native-msbuild",
                trustRequired = true,
                maximumFileBytes = WorkspaceFileSystem.MaximumFileBytes,
                message = service == null ? "Set XAMLG_STUDIO_WORKSPACE_ROOT to an existing directory before starting the companion." : "Local file access is enabled for this paired owner. SDK operations require explicit trust."
            });
            if (service == null) throw new InvalidOperationException("Local workspaces are disabled. Set XAMLG_STUDIO_WORKSPACE_ROOT before starting the companion.");
            object result;
            switch (action)
            {
                case "workspace_list":
                    result = service.Files.List(Read<PathArgs>(arguments).Path, cancellationToken); break;
                case "workspace_read":
                    result = await service.Files.ReadAsync(RequiredPath(Read<PathArgs>(arguments)), cancellationToken); break;
                case "workspace_write":
                    var write = Read<WriteArgs>(arguments);
                    result = await service.Files.WriteAsync(write.Path, write.ExpectedHash, write.File, write.Encoding, cancellationToken); break;
                case "workspace_delete":
                    var delete = Read<DeleteArgs>(arguments);
                    await service.Files.DeleteAsync(delete.Path, delete.ExpectedHash, cancellationToken);
                    result = new { accepted = true }; break;
                case "workspace_move":
                    var move = Read<MoveArgs>(arguments);
                    result = await service.Files.MoveAsync(move.Path, move.Destination, move.ExpectedHash, cancellationToken); break;
                case "workspace_evaluate":
                    result = await service.EvaluateAsync(Read<WorkspaceEvaluationRequest>(arguments), cancellationToken); break;
                case "workspace_build":
                    result = await service.BuildAsync(Read<WorkspaceBuildRequest>(arguments), cancellationToken); break;
                case "workspace_create":
                    result = await service.CreateAsync(Read<WorkspaceCreateRequest>(arguments), cancellationToken); break;
                case "workspace_edit":
                    result = await service.EditAsync(Read<WorkspaceSdkEditRequest>(arguments), cancellationToken); break;
                case "workspace_templates":
                    result = await service.TemplatesAsync(Read<TrustArgs>(arguments).Trust, cancellationToken); break;
                case "workspace_template_help":
                    var help = Read<TemplateHelpArgs>(arguments);
                    result = await service.TemplateHelpAsync(help.Template, help.Trust, cancellationToken); break;
                case "workspace_template_install":
                    var install = Read<TemplateInstallArgs>(arguments);
                    result = await service.InstallTemplatesAsync(install.Package, install.Version, install.Trust, cancellationToken); break;
                default: throw new ArgumentException("Unknown workspace action.");
            }
            return AutomationJson.Element(result);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or Win32Exception or NotSupportedException or System.Xml.XmlException)
        {
            throw new AutomationException("workspace_operation_failed", error is Win32Exception
                ? "The .NET SDK could not be started. Install a compatible SDK and restart the companion."
                : error.Message);
        }
    }

    private static T Read<T>(JsonElement arguments) => arguments.Deserialize<T>(AutomationJson.Options) ?? throw new ArgumentException("Missing workspace arguments.");
    private static string RequiredPath(PathArgs arguments) => arguments.Path ?? throw new ArgumentException("A workspace-relative path is required.");
    private sealed record PathArgs(string? Path = null);
    private sealed record TrustArgs(bool Trust = false);
    private sealed record WriteArgs(string Path, string? ExpectedHash, WorkspaceFile File, string? Encoding = null);
    private sealed record DeleteArgs(string Path, string ExpectedHash);
    private sealed record MoveArgs(string Path, string Destination, string ExpectedHash);
    private sealed record TemplateHelpArgs(string Template, bool Trust = false);
    private sealed record TemplateInstallArgs(string Package, string Version, bool Trust = false);
}
