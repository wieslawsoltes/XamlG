using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.RegularExpressions;

namespace XamlG.Automation;

/// <summary>A reusable typed catalog. Validation and the host authorization gate apply to every caller.</summary>
public sealed partial class AutomationCatalog : IAutomationHost, IAutomationCatalogEvents, IAutomationResourceEvents, IAutomationCompletions
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AutomationResource Resource, Func<string, AutomationCallContext, ValueTask<string>> Read)> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<string, string, AutomationCallContext, ValueTask<AutomationCompletion>>> _completions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AutomationPrompt> _prompts = new(StringComparer.Ordinal);
    private readonly Func<AutomationReview, CancellationToken, ValueTask<bool>>? _authorize;
    public AutomationCatalog(Func<AutomationReview, CancellationToken, ValueTask<bool>>? authorize = null) => _authorize = authorize;
    public event Action? CatalogChanged;
    public event Action<string>? ResourceChanged;
    public IReadOnlyList<AutomationTool> Tools { get { lock (_gate) return _tools.Values.Select(e => e.Tool).ToArray(); } }
    public IReadOnlyList<AutomationResource> Resources { get { lock (_gate) return _resources.Values.Select(e => e.Resource).ToArray(); } }
    public IReadOnlyList<AutomationPrompt> Prompts { get { lock (_gate) return _prompts.Values.ToArray(); } }

    public void Add<TArgs, TResult>(string name, string description, AutomationScope scope, AutomationEffect effect,
        Func<TArgs, AutomationCallContext, ValueTask<TResult>> execute, bool destructive = false)
    {
        if (!ToolName().IsMatch(name)) throw new ArgumentException("Tool names must match [a-zA-Z0-9_-]{1,64}.", nameof(name));
        ArgumentNullException.ThrowIfNull(execute);
        var node = AutomationJson.Options.GetJsonSchemaAsNode(typeof(TArgs));
        // A standalone reference type has nullable root metadata. MCP requires an object
        // schema, and CallAsync already rejects null argument envelopes.
        node["type"] = "object";
        var schema = JsonSerializer.SerializeToElement(node);
        var tool = new AutomationTool(name, description, schema, scope, effect, destructive);
        lock (_gate) _tools.Add(name, new(tool, async (arguments, context) =>
        {
            TArgs args;
            try { args = arguments.Deserialize<TArgs>(AutomationJson.Options) ?? throw new JsonException("Arguments cannot be null."); }
            catch (JsonException error) { throw new AutomationException("invalid_arguments", error.Message); }
            return AutomationJson.Element(await execute(args, context));
        }));
        NotifyCatalogChanged();
    }

    public void AddResource(AutomationResource resource, Func<AutomationCallContext, ValueTask<string>> read)
    { lock (_gate) _resources.Add(resource.Uri, (resource, (_, context) => read(context))); NotifyCatalogChanged(); }
    public void AddResourceTemplate(AutomationResource resource, Func<IReadOnlyDictionary<string, string>, AutomationCallContext, ValueTask<string>> read,
        Func<string, string, AutomationCallContext, ValueTask<AutomationCompletion>>? complete = null)
    {
        resource = resource with { IsTemplate = true };
        lock (_gate)
        {
            _resources.Add(resource.Uri, (resource, (uri, context) => read(AutomationUriTemplate.Match(resource.Uri, uri) ?? throw new AutomationException("unknown_resource", "Resource URI does not match."), context)));
            if (complete != null) _completions.Add(resource.Uri, complete);
        }
        NotifyCatalogChanged();
    }
    public ValueTask<AutomationCompletion> CompleteAsync(string resourceTemplate, string argument, string value, AutomationCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (argument.Length > 64 || value.Length > 2048) throw new ArgumentException("Completion arguments exceed their bounds.");
        Func<string, string, AutomationCallContext, ValueTask<AutomationCompletion>>? complete;
        lock (_gate) _completions.TryGetValue(resourceTemplate, out complete);
        return complete == null ? ValueTask.FromResult(new AutomationCompletion([], 0, false)) : complete(argument, value, context);
    }
    public void NotifyResourceChanged(string uri)
    {
        if (ResourceChanged is not { } observers) return;
        foreach (Action<string> observer in observers.GetInvocationList())
            try { observer(uri); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    public void AddPrompt(AutomationPrompt prompt)
    { lock (_gate) _prompts.Add(prompt.Name, prompt); NotifyCatalogChanged(); }

    public async ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, AutomationCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        Entry? entry;
        lock (_gate) _tools.TryGetValue(name, out entry);
        if (entry == null) throw new AutomationException("unknown_tool", "Unknown tool: " + name);
        AutomationSchema.Validate(entry.Tool.InputSchema, arguments);
        if (_authorize != null && !await _authorize(new(entry.Tool, arguments, context.Caller), context.CancellationToken))
            throw new AutomationException("permission_denied", "The host denied this tool invocation.");
        context.CancellationToken.ThrowIfCancellationRequested();
        return await entry.Execute(arguments, context);
    }

    public ValueTask<string> ReadResourceAsync(string uri, AutomationCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        // Resource readers should route to CallAsync, keeping the same authority and revision rules.
        Func<string, AutomationCallContext, ValueTask<string>>? read;
        lock (_gate) read = _resources.TryGetValue(uri, out var entry) ? entry.Read : _resources.Values.FirstOrDefault(item => AutomationUriTemplate.IsMatch(item.Resource, uri)).Read;
        return read != null ? read(uri, context) : ValueTask.FromException<string>(new AutomationException("unknown_resource", "Unknown resource: " + uri));
    }

    private void NotifyCatalogChanged()
    {
        if (CatalogChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* Observers cannot roll back a published registration. */ }
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolName();
    private sealed record Entry(AutomationTool Tool, Func<JsonElement, AutomationCallContext, ValueTask<JsonElement>> Execute);
}
