using System.Text.Json;
using System.Runtime.CompilerServices;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XamlG.Automation;

namespace XamlG.Mcp;

/// <summary>Synchronizes only this host's primitives. Other registrations remain intact;
/// updates emit at most one notification per changed collection.</summary>
internal sealed class AutomationMcpCatalogBinding : IDisposable
{
    private readonly IAutomationHost _host;
    private readonly object _gate = new();
    // HTTP creates options for every request. Weak collection keys release completed
    // requests and also let an embedding host intentionally share its SDK collections.
    private readonly ConditionalWeakTable<McpServerPrimitiveCollection<McpServerTool>, Dictionary<string, AutomationToolPrimitive>> _tools = new();
    private readonly ConditionalWeakTable<McpServerResourceCollection, Dictionary<string, AutomationResourcePrimitive>> _resources = new();
    private readonly ConditionalWeakTable<McpServerPrimitiveCollection<McpServerPrompt>, Dictionary<string, AutomationPromptPrimitive>> _prompts = new();
    private bool _disposed;
    public AutomationMcpCatalogBinding(IAutomationHost host)
    {
        _host = host;
        if (host is IAutomationCatalogEvents events) events.CatalogChanged += Refresh;
    }
    public void Attach(McpServerOptions options)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var tools = options.ToolCollection ??= new();
            var resources = options.ResourceCollection ??= new();
            var prompts = options.PromptCollection ??= new();
            Synchronize(tools, _tools.GetValue(tools, _ => new(StringComparer.Ordinal)), _host.Tools, t => t.Name, t => new AutomationToolPrimitive(_host, t), t => t.Definition);
            Synchronize(resources, _resources.GetValue(resources, _ => new(StringComparer.Ordinal)), _host.Resources, r => r.Uri, r => new AutomationResourcePrimitive(_host, r), r => r.Definition);
            Synchronize(prompts, _prompts.GetValue(prompts, _ => new(StringComparer.Ordinal)), _host.Prompts, p => p.Name, p => new AutomationPromptPrimitive(_host, p), p => p.Definition);
        }
    }
    private void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var tools = _host.Tools; var resources = _host.Resources; var prompts = _host.Prompts;
            foreach (var entry in _tools) Synchronize(entry.Key, entry.Value, tools, t => t.Name, t => new AutomationToolPrimitive(_host, t), t => t.Definition);
            foreach (var entry in _resources) Synchronize(entry.Key, entry.Value, resources, r => r.Uri, r => new AutomationResourcePrimitive(_host, r), r => r.Definition);
            foreach (var entry in _prompts) Synchronize(entry.Key, entry.Value, prompts, p => p.Name, p => new AutomationPromptPrimitive(_host, p), p => p.Definition);
        }
    }
    private static void Synchronize<TPrimitive, TOwned, TDefinition>(McpServerPrimitiveCollection<TPrimitive> collection,
        Dictionary<string, TOwned> owned, IEnumerable<TDefinition> definitions, Func<TDefinition, string> key,
        Func<TDefinition, TOwned> create, Func<TOwned, TDefinition> definition)
        where TPrimitive : IMcpServerPrimitive where TOwned : TPrimitive
    {
        var desired = definitions.ToDictionary(key, StringComparer.Ordinal);
        // Reject a collision before removing any current primitives. Foreign registrations
        // may share the SDK collection but remain owned by their original host.
        foreach (var name in desired.Keys)
            if (collection.TryGetPrimitive(name, out var existing) && (!owned.TryGetValue(name, out var current) || !ReferenceEquals(existing, current)))
                throw new InvalidOperationException("Duplicate MCP primitive: " + name);
        using var deferred = collection.DeferChangedEvents();
        foreach (var entry in owned.ToArray())
        {
            if (desired.TryGetValue(entry.Key, out var next) && EqualityComparer<TDefinition>.Default.Equals(definition(entry.Value), next)) continue;
            collection.Remove(entry.Value); owned.Remove(entry.Key);
        }
        foreach (var entry in desired)
        {
            if (owned.ContainsKey(entry.Key)) continue;
            var primitive = create(entry.Value);
            if (!collection.TryAdd(primitive)) throw new InvalidOperationException("Duplicate MCP primitive: " + entry.Key);
            owned.Add(entry.Key, primitive);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_host is IAutomationCatalogEvents events) events.CatalogChanged -= Refresh;
            _tools.Clear(); _resources.Clear(); _prompts.Clear();
        }
    }

    private sealed class AutomationToolPrimitive(IAutomationHost host, AutomationTool definition) : McpServerTool
    {
        public AutomationTool Definition { get; } = definition;
        public override IReadOnlyList<object> Metadata => [];
        public override Tool ProtocolTool { get; } = new()
        {
            Name = definition.Name, Description = definition.Description, InputSchema = definition.InputSchema,
            Annotations = new() { ReadOnlyHint = definition.Effect == AutomationEffect.Read, DestructiveHint = definition.Destructive,
                OpenWorldHint = definition.Effect == AutomationEffect.Execute }
        };
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await host.CallAsync(Definition.Name, AutomationJson.Element(request.Params.Arguments ?? new Dictionary<string, JsonElement>()), new("mcp", cancellationToken));
                return new() { StructuredContent = result, Content = [new TextContentBlock { Text = result.GetRawText() }] };
            }
            catch (AutomationException error) { return Error(error.Code, error.Message); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException)
            { return Error("operation_failed", error.Message); }
        }
    }
    private sealed class AutomationResourcePrimitive(IAutomationHost host, AutomationResource definition) : McpServerResource
    {
        public AutomationResource Definition { get; } = definition;
        public override IReadOnlyList<object> Metadata => [];
        public override ResourceTemplate ProtocolResourceTemplate { get; } = new()
        { UriTemplate = definition.Uri, Name = definition.Name, Description = definition.Description, MimeType = definition.MimeType };
        public override bool IsMatch(string uri) => AutomationUriTemplate.IsMatch(Definition, uri);
        public override async ValueTask<ReadResourceResult> ReadAsync(RequestContext<ReadResourceRequestParams> request, CancellationToken cancellationToken = default)
        {
            var resource = host.Resources.FirstOrDefault(r => AutomationUriTemplate.IsMatch(r, request.Params.Uri)) ?? throw new McpException("Unknown resource.");
            var text = await host.ReadResourceAsync(request.Params.Uri, new("mcp", cancellationToken));
            return new() { Contents = [new TextResourceContents { Uri = request.Params.Uri, MimeType = resource.MimeType, Text = text }] };
        }
    }
    private sealed class AutomationPromptPrimitive(IAutomationHost host, AutomationPrompt definition) : McpServerPrompt
    {
        public AutomationPrompt Definition { get; } = definition;
        public override IReadOnlyList<object> Metadata => [];
        public override Prompt ProtocolPrompt { get; } = new() { Name = definition.Name, Description = definition.Description };
        public override ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompt = host.Prompts.SingleOrDefault(p => p.Name == request.Params.Name) ?? throw new McpException("Unknown prompt.");
            return ValueTask.FromResult(new GetPromptResult { Description = prompt.Description,
                Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = prompt.Text } }] });
        }
    }
    private static CallToolResult Error(string code, string message)
    {
        var result = AutomationJson.Element(new { error = new { code, message } });
        return new() { IsError = true, StructuredContent = result, Content = [new TextContentBlock { Text = result.GetRawText() }] };
    }
}
