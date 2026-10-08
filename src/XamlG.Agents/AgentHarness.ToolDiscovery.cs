using XamlG.Automation;

namespace XamlG.Agents;

public sealed partial class AgentHarness
{
    private const string DiscoveryDescription = "Find IDE tools by words or name; enable named tools for subsequent requests. All visual/logical tree, properties, XAML/designer, C#/Roslyn, generated code, files, build and computer tools are available. Empty query lists tools with paging. Discovery does not grant permissions.";
    private static readonly HashSet<string> StarterTools = new(StringComparer.Ordinal)
    {
        "xamlg_project_get", "xamlg_document_read", "xamlg_source_edit", "xamlg_document_write",
        "xamlg_compiler_compile", "xamlg_runtime_run", "xamlg_build_read", "xamlg_wait",
        "xamlg_computer_observe", "xamlg_computer_actions", "xamlg_agent_plan", "xamlg_agent_question", "xamlg_agent_tools"
    };

    public IReadOnlyList<AutomationTool> RequestTools(AgentTask task, bool fullCatalog = false) =>
        SelectTools(task, ToolCatalog, fullCatalog);

    private static IReadOnlyList<AutomationTool> SelectTools(AgentTask task, IReadOnlyList<AutomationTool> catalog, bool full)
    {
        if (full || catalog.Count <= 24) return catalog;
        lock (task.Sync) return catalog.Where(tool => StarterTools.Contains(tool.Name) || task.EnabledTools.Contains(tool.Name)).ToArray();
    }

    private void AddDiscoveryTool(AutomationCatalog catalog, AgentTask task) =>
        catalog.Add<ToolDiscoveryArguments, object>("xamlg_agent_tools", DiscoveryDescription, AutomationScope.Agent, AutomationEffect.Read, (args, _) =>
        {
            if (args.Query.Length > 200 || args.Offset < 0 || args.Limit is < 1 or > 32 || args.Enable?.Length > 32)
                throw new ArgumentException("Use a short search, up to 32 names and a page of 1–32 tools.");
            var all = ToolCatalog;
            var names = all.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            if (args.Enable?.Any(name => !names.Contains(name)) == true) throw new ArgumentException("Unknown tool name. Search the catalog first.");
            lock (task.Sync) if (args.Enable != null) task.EnabledTools.UnionWith(args.Enable);
            var words = args.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matches = all.Where(tool => words.All(word => tool.Name.Contains(word, StringComparison.OrdinalIgnoreCase) || tool.Description.Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
            return ValueTask.FromResult<object>(new
            {
                total = matches.Length, offset = args.Offset,
                tools = matches.Skip(args.Offset).Take(args.Limit).Select(tool => new { tool.Name, tool.Description, tool.Scope, tool.Effect }),
                enabled = args.Enable ?? [], note = "Enabled schemas are included in the next request. Current permission policy still applies."
            });
        });

    public sealed record ToolDiscoveryArguments(string Query = "", string[]? Enable = null, int Offset = 0, int Limit = 20);
}
