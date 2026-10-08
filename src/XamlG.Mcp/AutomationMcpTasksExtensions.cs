using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

#pragma warning disable MCPEXP002 // Wrap the official Tasks extension request handlers with ownership checks.

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
        builder.Services.PostConfigure<McpServerOptions>(options =>
        {
            if (options.RequestHandlers is not { } handlers) return;
            for (var index = 0; index < handlers.Count; index++)
            {
                var handler = handlers[index];
                if (handler.Method is not (TasksProtocol.MethodTasksCancel or TasksProtocol.MethodTasksUpdate)) continue;
                // The SDK cancels its own execution token even when the store declines
                // cancellation. Check ownership before that independent side effect.
                handlers[index] = new()
                {
                    Method = handler.Method,
                    RoutingNameParameter = handler.RoutingNameParameter,
                    Handler = async (request, token) =>
                    {
                        if (request.Params is not JsonObject parameters || parameters["taskId"] is not JsonValue value ||
                            !value.TryGetValue<string>(out var id) || id is not { Length: > 0 and <= 128 } ||
                            await store.GetTaskAsync(id, token) == null)
                            throw new McpProtocolException("Unknown task.", McpErrorCode.InvalidParams);
                        return await handler.Handler(request, token);
                    }
                };
            }
        });
        return builder.WithTasks(store, options => options.ExecutionModeSelector = request =>
            request.Params.Name == "xamlg_wait" ? McpTaskExecutionMode.Optional : McpTaskExecutionMode.Synchronous);
    }
}
