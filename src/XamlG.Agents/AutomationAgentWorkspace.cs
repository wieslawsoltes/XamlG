using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Agents;

/// <summary>Revision-checked source checkpoints over the shared automation contract.</summary>
public class AutomationAgentWorkspace(IAutomationHost host) : IAgentWorkspace, IAgentOperationPreview
{
    public async Task<JsonElement?> PreviewOperationAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!host.Tools.Any(tool => tool.Name == "xamlg_operation_preview")) return null;
        return await host.CallAsync("xamlg_operation_preview", AutomationJson.Element(new { name = tool, arguments }), new("Agent operation review", cancellationToken));
    }
    public async Task<AgentWorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var value = await host.CallAsync("xamlg_project_export", AutomationJson.Element(new { }), new("Agent checkpoint", cancellationToken));
        return new(value.GetProperty("revision").GetInt64(), value.GetProperty("documents").Deserialize<Dictionary<string, string>>(AutomationJson.Options)!);
    }
    public async Task<AgentWorkspaceSnapshot> RestoreAsync(long expectedRevision, IReadOnlyList<AgentFileChange> files, CancellationToken cancellationToken)
    {
        var value = await host.CallAsync("xamlg_project_restore", AutomationJson.Element(new { expectedRevision, files }), new("Agent change review", cancellationToken));
        return new(value.GetProperty("revision").GetInt64(), value.GetProperty("documents").Deserialize<Dictionary<string, string>>(AutomationJson.Options)!);
    }
}
