using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XamlG.Automation;

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
            // The MCP SDK uses a generated JSON contract. An arbitrary anonymous CLR type
            // cannot be serialized by that contract; JsonElement is the explicit wire value.
            options.Capabilities.Extensions.TryAdd("io.modelcontextprotocol/ui",
                AutomationJson.Element(new { mimeTypes = new[] { "text/html;profile=mcp-app" } }));
        });
        return builder;
    }
}
