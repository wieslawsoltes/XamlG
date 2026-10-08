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
            var partial = new StringBuilder(); var completed = false; var partialTruncated = false; var usageAccounted = false;
            AgentProviderException failure;
            try
            {
                Publish(task, "request", $"Request {budget.Requests}; estimated input {inputEstimate:N0} tokens, output allowance {effective.MaxOutputTokens:N0}.");
                var reply = await task.Provider.GenerateAsync(effective, text =>
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    outputBytes = checked(outputBytes + Encoding.UTF8.GetByteCount(text));
                    if (outputBytes > 8_388_608) throw new AgentProviderException("response_too_large", false, canResume: false);
                    if (!checkpoint)
                    {
                        var take = Math.Min(text.Length, 262000 - partial.Length);
                        partial.Append(text, 0, take); partialTruncated |= take != text.Length;
                        Publish(task, "text_delta", text);
                    }
                    return ValueTask.CompletedTask;
                }, timeout.Token);
                AccountUsage(task, reply.Usage); usageAccounted = true;
                // A transport can finish concurrently with revocation or ignore its
                // timeout token. Preserve reported usage, but never commit that reply.
                timeout.Token.ThrowIfCancellationRequested();
                if (reply.OutputLimitReached)
                {
                    if (!checkpoint && partial.Length == 0)
                    { partial.Append(reply.Text.AsSpan(0, Math.Min(reply.Text.Length, 262000))); partialTruncated |= reply.Text.Length > 262000; }
                    if (!checkpoint) task.OutputLimitToExceed = reply.OutputLimitCanBeIncreased ? effective.MaxOutputTokens : null;
                    Pause(task, checkpoint ? "The checkpoint reached its output limit. Existing context was preserved." :
                        reply.OutputLimitCanBeIncreased ? $"Provider output limit reached. Raise the effective output allowance above {effective.MaxOutputTokens:N0} and resume; incomplete output was not committed." :
                        "Provider output limit reached. This route does not accept an output cap. Review and resume the pending request; incomplete output was not committed.");
                    return null;
                }
                if (!checkpoint) task.OutputLimitToExceed = null;
                completed = true;
                return reply;
            }
            catch (AgentProviderException error)
            {
                if (!usageAccounted) AccountUsage(task, error.Usage ?? new(inputEstimate, (outputBytes + 3) / 4, true));
                failure = error;
            }
            catch (OperationCanceledException)
            {
                if (!usageAccounted) AccountUsage(task, new(inputEstimate, (outputBytes + 3) / 4, true));
                lease.Token.ThrowIfCancellationRequested();
                failure = new("request_timeout", true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!usageAccounted) AccountUsage(task, new(inputEstimate, (outputBytes + 3) / 4, true)); throw; }
            finally
            {
                if (!completed && partial.Length != 0)
                    Publish(task, "assistant_incomplete", partial.ToString() + (partialTruncated ? "\n[public preview truncated]" : ""));
            }
            lease.Token.ThrowIfCancellationRequested();
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
