using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Server;

namespace XamlG.Mcp;

public static class AutomationMcpTasksExtensions
{
    /// <summary>Enables the official Tasks extension for xamlg_wait only. Other tools
    /// remain synchronous. The embedding application owns and disposes the store.</summary>
    public static IMcpServerBuilder WithAutomationTasks(this IMcpServerBuilder builder, AutomationMcpTaskStore store,
        Func<CancellationToken>? workspaceLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);
        builder.Services.TryAddSingleton<AutomationMcpSubscriptions>();
        builder.Services.AddOptions<McpServerOptions>().Configure<IServiceProvider>((options, services) =>
        {
            var subscriptions = services.GetRequiredService<AutomationMcpSubscriptions>();
            subscriptions.Register(store);
            options.Handlers.SubscriptionsListenHandler ??= subscriptions.ListenAsync;
            options.Filters.Message.IncomingFilters.Add(next => async (context, token) =>
            {
                using var scope = store.Enter(context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "mcp", workspaceLifetime?.Invoke() ?? default);
                await next(context, token);
            });
            options.Filters.Request.CallToolFilters.Add(next => async (request, token) =>
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, store.WorkspaceLifetime, store.TaskLifetime);
                lifetime.Token.ThrowIfCancellationRequested();
                return await next(request, lifetime.Token);
            });
        });
        return builder.WithTasks(store, options => options.ExecutionModeSelector = request =>
            request.Params.Name == "xamlg_wait" ? McpTaskExecutionMode.Optional : McpTaskExecutionMode.Synchronous);
    }
}
