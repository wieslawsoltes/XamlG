using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XamlG.Automation;

namespace XamlG.Mcp;

/// <summary>Exposes an automation host unchanged through the official SDK's transports.</summary>
public static class AutomationMcpExtensions
{
    public static IMcpServerBuilder WithAutomation(this IMcpServerBuilder builder, IAutomationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return builder
            .WithListToolsHandler((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ListToolsResult
                {
                    Tools = host.Tools.Select(tool => new Tool
                    {
                        Name = tool.Name, Description = tool.Description, InputSchema = tool.InputSchema,
                        Annotations = new ToolAnnotations
                        {
                            ReadOnlyHint = tool.Effect == AutomationEffect.Read,
                            DestructiveHint = tool.Destructive,
                            OpenWorldHint = tool.Effect == AutomationEffect.Execute
                        }
                    }).ToArray()
                });
            })
            .WithCallToolHandler(async (request, cancellationToken) =>
            {
                try
                {
                    var args = AutomationJson.Element(request.Params.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>());
                    var result = await host.CallAsync(request.Params.Name, args, new("mcp", cancellationToken));
                    return new CallToolResult
                    {
                        StructuredContent = result,
                        Content = [new TextContentBlock { Text = result.GetRawText() }]
                    };
                }
                catch (AutomationException error) { return Error(error.Code, error.Message); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException)
                { return Error("operation_failed", error.Message); }
            })
            .WithListResourcesHandler((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ListResourcesResult
                {
                    Resources = host.Resources.Select(resource => new Resource
                    {
                        Uri = resource.Uri, Name = resource.Name, Description = resource.Description, MimeType = resource.MimeType
                    }).ToArray()
                });
            })
            .WithReadResourceHandler(async (request, cancellationToken) =>
            {
                var resource = host.Resources.SingleOrDefault(r => r.Uri == request.Params.Uri)
                    ?? throw new McpException("Unknown resource.");
                var text = await host.ReadResourceAsync(resource.Uri, new("mcp", cancellationToken));
                return new ReadResourceResult
                {
                    Contents = [new TextResourceContents { Uri = resource.Uri, MimeType = resource.MimeType, Text = text }]
                };
            })
            .WithListPromptsHandler((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ListPromptsResult
                {
                    Prompts = host.Prompts.Select(prompt => new Prompt { Name = prompt.Name, Description = prompt.Description }).ToArray()
                });
            })
            .WithGetPromptHandler((request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prompt = host.Prompts.SingleOrDefault(p => p.Name == request.Params.Name)
                    ?? throw new McpException("Unknown prompt.");
                return ValueTask.FromResult(new GetPromptResult
                {
                    Description = prompt.Description,
                    Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = prompt.Text } }]
                });
            });
    }

    private static CallToolResult Error(string code, string message)
    {
        var result = AutomationJson.Element(new { error = new { code, message } });
        return new() { IsError = true, StructuredContent = result, Content = [new TextContentBlock { Text = result.GetRawText() }] };
    }
}
