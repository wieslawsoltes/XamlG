using System.Text.Json;
using XamlG.Agents;
using XamlG.Agents.OpenAI;
using XamlG.Automation;
using XamlG.Mcp;

namespace XamlG.Studio.Host;

/// <summary>Companion account and MCP-operation integration over the reusable workbench session.</summary>
public sealed class AgentWorkbench : AgentWorkbenchSession
{
    private readonly AutomationMcpTaskStore? _mcpTasks;
    private readonly ChatGptAccountManager? _chatGpt;
    private readonly string? _chatGptError;
    public AgentWorkbench(IAutomationHost host, IEnumerable<IAgentProvider> providers, IAgentWorkspace? workspace = null, AutomationMcpTaskStore? mcpTasks = null,
        ChatGptAccountManager? chatGpt = null, string? chatGptError = null) : base(host, providers, workspace, new AgentPermissionConstraints(deniedTools: ["xamlg_layout_set", "xamlg_layout_reset"]))
    { _mcpTasks = mcpTasks; _chatGpt = chatGpt; _chatGptError = chatGptError; }
    protected override IEnumerable<string> ProviderIds => base.ProviderIds.Concat(_chatGpt == null ? [] : new[] { ChatGptAccountAgentProvider.ProviderId });
    protected override object? AccountState => _chatGpt?.State;
    protected override string? AccountError => _chatGptError;
    protected override object? OperationState => _mcpTasks?.LocalInventory.Select(task => new { task.TaskId, status = task.Status.ToString(), task.CreatedAt, task.LastUpdatedAt });
    protected override object? DescribeAccount(IAgentProvider provider) => provider is ChatGptAccountAgentProvider account
        ? new { id = account.AccountId, label = account.AccountLabel + " · " + account.AccountId[..8] } : null;
    protected override async Task<object> ModelChoicesAsync(ProviderArgs args, CancellationToken cancellationToken) =>
        Provider(args.Provider, args.AccountId) is ChatGptAccountAgentProvider account
            ? await account.ListModelChoicesAsync(cancellationToken) : await base.ModelChoicesAsync(args, cancellationToken);
    protected override IAgentProvider Provider(string id, string? accountId = null) => id == ChatGptAccountAgentProvider.ProviderId
        ? Accounts().CreateProvider(accountId ?? throw new ArgumentException("Select an explicit ChatGPT account for this request."))
        : base.Provider(id, accountId);
    private ChatGptAccountManager Accounts() => _chatGpt ?? throw new InvalidOperationException(_chatGptError ?? "ChatGPT account mode is disabled in the companion.");

    protected override async ValueTask<JsonElement> ExecuteExtensionAsync(string action, JsonElement arguments, CancellationToken cancellationToken, CancellationToken ownerSession)
    {
        switch (action)
        {
            case "chatgpt_sign_in":
                var signIn = Read<AccountSignInArgs>(arguments);
                return AutomationJson.Element(await Accounts().BeginSignInAsync(signIn.AccountId, signIn.Label, signIn.Remember, signIn.RetrySignInId, signIn.RequestPlanConsent, ownerSession, cancellationToken));
            case "chatgpt_cancel_sign_in": await Accounts().CancelSignInAsync(Read<IdArgs>(arguments).Id, cancellationToken); break;
            case "chatgpt_select": await Accounts().SelectAsync(Read<IdArgs>(arguments).Id, cancellationToken); break;
            case "chatgpt_configure":
                var configure = Read<AccountConfigureArgs>(arguments);
                await Accounts().ConfigureAsync(configure.Id, configure.Label, configure.Remember, cancellationToken); break;
            case "chatgpt_sign_out": return AutomationJson.Element(await Accounts().SignOutAsync(Read<IdArgs>(arguments).Id, cancellationToken));
            case "operation_cancel": _mcpTasks?.CancelLocal(Read<IdArgs>(arguments).Id); break;
            case "operations_clear": _mcpTasks?.ClearFinishedLocal(); break;
            default: return await base.ExecuteExtensionAsync(action, arguments, cancellationToken, ownerSession);
        }
        return AutomationJson.Element(new { accepted = true });
    }
    public sealed record AccountSignInArgs(string? AccountId = null, string? Label = null, bool Remember = false, string? RetrySignInId = null, bool RequestPlanConsent = false);
    public sealed record AccountConfigureArgs(string Id, string Label, bool Remember);
}

public sealed class BrowserAgentWorkspace(IAutomationHost host) : AutomationAgentWorkspace(host);
