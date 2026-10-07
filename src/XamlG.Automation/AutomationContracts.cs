using System.Text.Json;
using System.Text.Json.Serialization;

namespace XamlG.Automation;

public enum AutomationScope { Project, Source, Designer, Compiler, Runtime, Layout, Build, Agent }
public enum AutomationEffect { Read, Edit, Execute }
public enum PermissionDecision { Allow, Ask, Deny }
public enum PermissionProfile { Ask, ReadOnly, Plan, AutoEdit, FullAccess, Custom }

public sealed record AutomationTool(string Name, string Description, JsonElement InputSchema,
    AutomationScope Scope, AutomationEffect Effect, bool Destructive = false);
public sealed record AutomationResource(string Uri, string Name, string Description, string MimeType = "application/json", bool IsTemplate = false);
public sealed record AutomationPrompt(string Name, string Description, string Text);
public sealed record AutomationCallContext(string Caller, CancellationToken CancellationToken = default);
public sealed record AutomationReview(AutomationTool Tool, JsonElement Arguments, string Caller);

/// <summary>Transport-independent, browser-compatible automation boundary.</summary>
public interface IAutomationHost
{
    IReadOnlyList<AutomationTool> Tools { get; }
    IReadOnlyList<AutomationResource> Resources { get; }
    IReadOnlyList<AutomationPrompt> Prompts { get; }
    ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, AutomationCallContext context);
    ValueTask<string> ReadResourceAsync(string uri, AutomationCallContext context);
}

public sealed class AutomationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Optional dynamic discovery. Raise after publishing the complete catalog change;
/// this event contains no project source or credentials.</summary>
public interface IAutomationCatalogEvents
{
    event Action? CatalogChanged;
}

public interface IAutomationResourceEvents
{
    event Action<string>? ResourceChanged;
}

public sealed record AutomationCompletion(IReadOnlyList<string> Values, int Total, bool HasMore);
public interface IAutomationCompletions
{
    ValueTask<AutomationCompletion> CompleteAsync(string resourceTemplate, string argument, string value, AutomationCallContext context);
}

public static class AutomationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            PropertyNameCaseInsensitive = false,
            MaxDepth = 64,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
