using System.Text.Json;

namespace XamlG.Agents;

/// <summary>Private durable session, including opaque provider continuations. Never use as a public transcript.</summary>
public sealed record AgentSessionSnapshot(int Version, IReadOnlyList<AgentTaskSnapshot> Tasks);
public sealed record AgentStoredMessage(AgentMessageKind Kind, string Text, string? ToolCallId, JsonElement? Native);
public sealed record AgentStoredReply(string Text, IReadOnlyList<AgentToolCall> ToolCalls, AgentUsage Usage, JsonElement Native);
public sealed record AgentTaskSnapshot
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public string? Account { get; init; }
    public required string Model { get; init; }
    public AgentTaskStatus Status { get; init; }
    public string? StatusReason { get; init; }
    public bool PreviousWorkspace { get; init; }
    public long ReportedTokens { get; init; }
    public long EstimatedTokens { get; init; }
    public AgentUsage? LastUsage { get; init; }
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public int? OutputLimitToExceed { get; init; }
    public int NativeContextBytes { get; init; }
    public long PlanRevision { get; init; }
    public int CheckpointCount { get; init; }
    public string Draft { get; init; } = "";
    public IReadOnlyList<AgentPlanStep> Plan { get; init; } = [];
    public IReadOnlyList<AgentEvent> Events { get; init; } = [];
    public AgentQueueSnapshot Queue { get; init; } = new(0, []);
    public AgentChangeReview? Changes { get; init; }
    public AgentChangeReview? LatestRunChanges { get; init; }
    public AgentWorkspaceSnapshot? BeforeRun { get; init; }
    public AgentWorkspaceSnapshot? BeforeLatestRun { get; init; }
    public IReadOnlyList<AgentStoredMessage> Messages { get; init; } = [];
    public int? ActiveRequestIndex { get; init; }
    public IReadOnlyList<string> UserRequests { get; init; } = [];
    public AgentStoredReply? PendingReply { get; init; }
    public int NextTool { get; init; }
    public string? ExecutingToolId { get; init; }
    public string? Goal { get; init; }
    public string? LatestRequest { get; init; }
    public IReadOnlyList<string> EnabledTools { get; init; } = [];
}
