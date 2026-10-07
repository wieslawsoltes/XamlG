using System.Text;
using XamlG.Automation;

namespace XamlG.Agents;

public sealed partial class AgentHarness
{
    private sealed class RunBudget { public int Requests; }

    private async Task<AgentReply?> RequestProviderAsync(AgentTask task, AgentRequest request, AgentRunOptions options,
        AutomationLease lease, RunBudget budget, bool checkpoint = false)
    {
        for (var retry = 0; ; retry++)
        {
            lease.Token.ThrowIfCancellationRequested();
            if (task.RetryAfterUtc is { } cooldown && cooldown > DateTimeOffset.UtcNow)
            {
                var wait = cooldown - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.FromMinutes(5) || cooldown >= lease.ExpiresAt || budget.Requests >= options.Limits.RequestsPerRun)
                { Pause(task, $"Provider cooldown lasts until {cooldown:O}. Resume after this deadline."); return null; }
                Publish(task, "retry_wait", $"Waiting until {cooldown:O} before another request.");
                await Task.Delay(wait, lease.Token);
            }
            task.RetryAfterUtc = null;
            if (budget.Requests >= options.Limits.RequestsPerRun) { Pause(task, "Request limit reached."); return null; }
            if (task.TotalTokens >= options.Limits.TotalTaskTokens) { Pause(task, "Task token budget reached."); return null; }
            var inputEstimate = (task.Provider.GetContextBytes(request) + 3L) / 4;
            var remaining = options.Limits.TotalTaskTokens - task.TotalTokens - inputEstimate;
            if (remaining < 1) { Pause(task, "The remaining task budget cannot fit the estimated input. Review the token limit."); return null; }
            var effective = request with { MaxOutputTokens = (int)Math.Min(request.MaxOutputTokens, remaining) };
            if (!checkpoint && task.OutputLimitToExceed is { } previous && effective.MaxOutputTokens <= previous)
            { Pause(task, $"The effective output allowance must exceed {previous:N0} tokens. Raise output and remaining task limits."); return null; }
            budget.Requests++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
            timeout.CancelAfter(options.Limits.RequestTimeout);
            long outputBytes = 0;
            AgentProviderException failure;
            try
            {
                Publish(task, "request", $"Request {budget.Requests}; estimated input {inputEstimate:N0} tokens, output allowance {effective.MaxOutputTokens:N0}.");
                var reply = await task.Provider.GenerateAsync(effective, text =>
                {
                    outputBytes = checked(outputBytes + Encoding.UTF8.GetByteCount(text));
                    if (outputBytes > 8_388_608) throw new AgentProviderException("response_too_large", false, canResume: false);
                    if (!checkpoint) Publish(task, "text_delta", text); return ValueTask.CompletedTask;
                }, timeout.Token);
                AccountUsage(task, reply.Usage);
                if (reply.OutputLimitReached)
                {
                    if (!checkpoint) task.OutputLimitToExceed = effective.MaxOutputTokens;
                    Pause(task, checkpoint ? "The checkpoint reached its output limit. Existing context was preserved." :
                        $"Provider output limit reached. Raise the effective output allowance above {effective.MaxOutputTokens:N0} and resume; incomplete output was not committed.");
                    return null;
                }
                if (!checkpoint) task.OutputLimitToExceed = null;
                return reply;
            }
            catch (AgentProviderException error)
            {
                AccountUsage(task, error.Usage ?? new(inputEstimate, (outputBytes + 3) / 4, true));
                failure = error;
            }
            catch (OperationCanceledException)
            {
                AccountUsage(task, new(inputEstimate, (outputBytes + 3) / 4, true));
                lease.Token.ThrowIfCancellationRequested();
                failure = new("request_timeout", true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { AccountUsage(task, new(inputEstimate, (outputBytes + 3) / 4, true)); throw; }
            if (!failure.CanResume) throw failure;
            var delay = failure.RetryAfter ?? TimeSpan.FromMilliseconds(Math.Min(30000, 500 * Math.Pow(2, retry)) + Random.Shared.Next(250));
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            var now = DateTimeOffset.UtcNow;
            task.RetryAfterUtc = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
            if (!failure.Retryable || retry >= options.Limits.AutomaticRetries)
            { Pause(task, failure.Message + " Resume requires a fresh review and respects the retry deadline."); return null; }
            Publish(task, "retry", $"Retry {retry + 1}: {failure.Code}");
        }
    }

    private static void AccountUsage(AgentTask task, AgentUsage usage)
    {
        if (usage.InputTokens < 0 || usage.OutputTokens < 0) throw new AutomationException("invalid_response", "Invalid provider usage.");
        if (usage.Estimated) task.EstimatedTokens = checked(task.EstimatedTokens + usage.Total);
        else task.ReportedTokens = checked(task.ReportedTokens + usage.Total);
        task.LastUsage = usage;
    }

    private bool ReserveToolResults(AgentTask task, AgentRunOptions options, IReadOnlyList<AutomationTool> tools)
    {
        var request = new AgentRequest(task.Model, options.Instructions, task.Messages.ToArray(), tools, options.Limits.OutputTokensPerRequest);
        task.NativeContextBytes = task.Provider.GetContextBytes(request);
        var otherTasks = _tasks.Values.Where(other => other != task).Sum(other => (long)other.NativeContextBytes);
        var available = Math.Min(options.Limits.ContextBytes, 64_000_000 - otherTasks) - task.NativeContextBytes;
        var count = task.PendingReply!.ToolCalls.Count - task.NextTool;
        // JSON escaping can expand a byte sixfold. Reserve every result before
        // any operation executes, including the explicit omission envelope.
        var each = available / Math.Max(1, count) - 2048;
        if (each < 256 * 6)
        { Pause(task, "The pending tool batch cannot fit bounded results in the retained context. Compact complete context or raise the context limit."); return false; }
        task.PendingResultBytes = (int)Math.Min(options.Limits.ToolResultBytes, each / 6);
        return true;
    }
}
