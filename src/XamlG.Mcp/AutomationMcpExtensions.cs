using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using XamlG.Automation;

namespace XamlG.Mcp;

/// <summary>Exposes automation through the official SDK's transports and dynamic primitive
/// collections. Catalog notifications support both legacy sessions and modern HTTP/stdio
/// subscriptions. An explicitly registered subscription handler takes precedence.</summary>
public static class AutomationMcpExtensions
{
    public static IMcpServerBuilder WithAutomation(this IMcpServerBuilder builder, IAutomationHost host)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(host);
        // DI owns the observer and unsubscribes it when the embedding host is disposed.
        var key = new object();
        builder.Services.AddKeyedSingleton<AutomationMcpCatalogBinding>(key, (_, _) => new(host));
        builder.Services.TryAddSingleton<AutomationMcpSubscriptions>();
        builder.Services.AddOptions<McpServerOptions>().Configure<IServiceProvider>((options, services) =>
        {
            services.GetRequiredKeyedService<AutomationMcpCatalogBinding>(key).Attach(options);
            var subscriptions = services.GetRequiredService<AutomationMcpSubscriptions>(); subscriptions.Register(host);
            options.Handlers.SubscriptionsListenHandler ??= subscriptions.ListenAsync;
            if (host is IAutomationResourceEvents)
            {
                options.Handlers.SubscribeToResourcesHandler ??= subscriptions.SubscribeAsync;
                options.Handlers.UnsubscribeFromResourcesHandler ??= subscriptions.UnsubscribeAsync;
            }
            if (host is IAutomationCompletions) options.Handlers.CompleteHandler ??= subscriptions.CompleteAsync;
            AutomationMcpPagination.Attach(options);
        });
        return builder;
    }
}
