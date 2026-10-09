using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace XamlG.Mcp;

public static class AutomationMcpUiExtensions
{
    /// <summary>Advertise the optional MCP Apps extension while retaining text-only compatibility.
    /// Tool/resource metadata and the host's authorization still determine what a client can display or invoke.</summary>
    public static IMcpServerBuilder WithAutomationUi(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<McpServerOptions>().Configure(options =>
        {
            options.Capabilities ??= new();
            options.Capabilities.Extensions ??= new Dictionary<string, object>(StringComparer.Ordinal);
            options.Capabilities.Extensions.TryAdd("io.modelcontextprotocol/ui", new { mimeTypes = new[] { "text/html;profile=mcp-app" } });
        });
        return builder;
    }
}
