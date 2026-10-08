using XamlG.Agents;
using XamlG.Automation;

namespace XamlG.Automation.Tests;

internal sealed class ScriptedAgentProvider : IAgentProvider, IAgentProviderSession
{
    private readonly Queue<Func<AgentRequest, Func<string, ValueTask>, CancellationToken, Task<AgentReply>>> _steps = new();
    public string Id => "scripted";
    public List<AgentRequest> Requests { get; } = [];
    public Func<AgentRequest, int> ContextSize { get; set; } = _ => 100;
    public CancellationToken SessionLifetime { get; set; }
    public int GetContextBytes(AgentRequest request) => ContextSize(request);
    public CancellationToken GetSessionLifetime() => SessionLifetime;
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(["fixture"]);
    public void Add(AgentReply reply) => Add((_, _, _) => Task.FromResult(reply));
    public void Add(Func<AgentRequest, Func<string, ValueTask>, CancellationToken, Task<AgentReply>> step) => _steps.Enqueue(step);
    public Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> delta, CancellationToken cancellationToken)
    {
        Requests.Add(request with { Messages = request.Messages.ToArray(), Tools = request.Tools.ToArray() });
        return _steps.Dequeue()(request, delta, cancellationToken);
    }
    public static AgentReply Done(string text = "Done", object? native = null) => new(text, [], new(10, 5), native ?? new object());
    public static AgentReply Call(params AgentToolCall[] calls) => new("", calls, new(10, 5), new object());
}

internal sealed class AgentTestWorkspace : IAgentWorkspace
{
    public Dictionary<string, string> Documents { get; } = new(StringComparer.Ordinal);
    public long Revision { get; private set; }
    public Func<CancellationToken, Task>? BeforeCapture { get; set; }
    public List<IReadOnlyList<AgentFileChange>> Restores { get; } = [];
    public void Edit(string path, string? text)
    {
        if (text == null) Documents.Remove(path); else Documents[path] = text;
        Revision++;
    }
    public async Task<AgentWorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        if (BeforeCapture != null) await BeforeCapture(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(Revision, new Dictionary<string, string>(Documents, StringComparer.Ordinal));
    }
    public Task<AgentWorkspaceSnapshot> RestoreAsync(long expectedRevision, IReadOnlyList<AgentFileChange> files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Revision != expectedRevision || files.Any(file => Documents.GetValueOrDefault(file.Path) != file.After))
            throw new AutomationException("revision_conflict", "Source changed.");
        Restores.Add(files.ToArray());
        foreach (var file in files)
            if (file.Before == null) Documents.Remove(file.Path); else Documents[file.Path] = file.Before;
        Revision++;
        return CaptureAsync(cancellationToken);
    }
}
