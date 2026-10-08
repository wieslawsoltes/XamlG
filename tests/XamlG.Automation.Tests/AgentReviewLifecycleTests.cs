using XamlG.Agents;
using XamlG.Automation;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AgentReviewLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Task_start_and_latest_run_baselines_include_manual_edits_and_restore_one_exact_block()
    {
        var workspace = new AgentTestWorkspace();
        workspace.Edit("View.axaml", "first\r\nstable\nlast"); workspace.Edit("Other.cs", "untouched");
        var provider = new ScriptedAgentProvider();
        provider.Add((_, _, _) => { workspace.Edit("View.axaml", "agent first\r\nstable\nagent last"); return Task.FromResult(ScriptedAgentProvider.Done()); });
        provider.Add((_, _, _) => { workspace.Edit("View.axaml", "agent first\r\nstable\nsecond last\n"); return Task.FromResult(ScriptedAgentProvider.Done()); });
        using var harness = new AgentHarness(new AutomationCatalog(), workspace);
        var task = harness.CreateTask("Review", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "First edit", new(), cancellationToken: Token);
        var first = harness.GetChangeReview(task.Id);
        Assert.Same(first, await harness.RefreshChangesAsync(task.Id, cancellationToken: Token));
        workspace.Edit("Other.cs", "manual unrelated edit");
        var refreshed = await harness.RefreshChangesAsync(task.Id, cancellationToken: Token);
        Assert.NotEqual(first.ReviewId, refreshed.ReviewId);
        Assert.Equal(first.FileIdentities["View.axaml"], refreshed.FileIdentities["View.axaml"]);
        await harness.RunAsync(task.Id, "Second edit", new(), cancellationToken: Token);
        var wholeTask = harness.GetChangeReview(task.Id);
        var latest = harness.GetChangeReview(task.Id, latestRun: true);
        Assert.Equal("first\r\nstable\nlast", wholeTask.Files.Single(file => file.Path == "View.axaml").Before);
        Assert.Equal("agent first\r\nstable\nagent last", Assert.Single(latest.Files).Before);
        Assert.Equal(2, wholeTask.Files.Count);
        var block = Assert.Single(AgentSourceReview.Diff(latest.Files[0]).Blocks);
        var restored = await harness.RestoreBlockAsync(task.Id, "View.axaml", block.Id, latest.ReviewId, latest.Revision, Token, latestRun: true);
        Assert.Empty(restored.Files);
        Assert.Equal("agent first\r\nstable\nagent last", workspace.Documents["View.axaml"]);
        Assert.Equal("manual unrelated edit", workspace.Documents["Other.cs"]);
        Assert.Equal("agent first\r\nstable\nsecond last\n", Assert.Single(Assert.Single(workspace.Restores)).After);
        Assert.Equal(2, harness.GetChangeReview(task.Id).Files.Count);
    }

    [Fact]
    public async Task Whole_file_restore_handles_creation_deletion_and_empty_documents_atomically()
    {
        var workspace = new AgentTestWorkspace(); workspace.Edit("Deleted.cs", ""); workspace.Edit("Keep.cs", "before");
        var provider = new ScriptedAgentProvider();
        provider.Add((_, _, _) =>
        {
            workspace.Edit("Deleted.cs", null); workspace.Edit("Added.cs", ""); workspace.Edit("Keep.cs", "unselected change");
            return Task.FromResult(ScriptedAgentProvider.Done());
        });
        using var harness = new AgentHarness(new AutomationCatalog(), workspace);
        var task = harness.CreateTask("Files", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Change files", new(), cancellationToken: Token);
        var review = harness.GetChangeReview(task.Id);
        var result = await harness.RestoreChangesAsync(task.Id, ["Added.cs", "Deleted.cs"], review.Revision, Token, expectedReviewId: review.ReviewId);
        Assert.Equal("Keep.cs", Assert.Single(result.Files).Path);
        Assert.False(workspace.Documents.ContainsKey("Added.cs")); Assert.Equal("", workspace.Documents["Deleted.cs"]);
        Assert.Equal("unselected change", workspace.Documents["Keep.cs"]); Assert.Equal(2, Assert.Single(workspace.Restores).Count);
    }

    [Fact]
    public async Task Stale_comparison_revision_and_full_document_guards_prevent_restore()
    {
        var workspace = new AgentTestWorkspace(); workspace.Edit("View.axaml", "before\n");
        var provider = new ScriptedAgentProvider();
        provider.Add((_, _, _) => { workspace.Edit("View.axaml", "after\n"); return Task.FromResult(ScriptedAgentProvider.Done()); });
        using var harness = new AgentHarness(new AutomationCatalog(), workspace);
        var task = harness.CreateTask("Guards", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Change", new(), cancellationToken: Token);
        var review = harness.GetChangeReview(task.Id); var block = Assert.Single(AgentSourceReview.Diff(review.Files[0]).Blocks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RestoreBlockAsync(task.Id, "View.axaml", block.Id, "wrong review", review.Revision, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RestoreBlockAsync(task.Id, "View.axaml", block.Id, review.ReviewId, review.Revision + 1, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.RestoreChangesAsync(task.Id, ["View.axaml", "View.axaml"], review.Revision, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.RestoreChangesAsync(task.Id, ["View.axaml", "Unknown.cs"], review.Revision, Token));
        // Even a broken embedding host that fails to increment its revision cannot defeat
        // the complete source guard passed by selective restoration.
        workspace.Documents["View.axaml"] = "manual edit without revision";
        await Assert.ThrowsAsync<AutomationException>(() => harness.RestoreBlockAsync(task.Id, "View.axaml", block.Id, review.ReviewId, review.Revision, Token));
        Assert.Empty(workspace.Restores);
        workspace.Edit("View.axaml", "manual revised edit");
        await Assert.ThrowsAsync<AutomationException>(() => harness.RestoreBlockAsync(task.Id, "View.axaml", block.Id, review.ReviewId, review.Revision, Token));
        var fresh = await harness.RefreshChangesAsync(task.Id, cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RestoreBlockAsync(task.Id, "View.axaml", block.Id, review.ReviewId, fresh.Revision, Token));
        Assert.Equal("manual revised edit", workspace.Documents["View.axaml"]); Assert.Empty(workspace.Restores);
    }

    [Fact]
    public async Task Comparisons_belong_to_their_task_and_checkpoint_even_when_the_revision_is_unchanged()
    {
        var workspace = new AgentTestWorkspace(); workspace.Edit("View.axaml", "before");
        var provider = new ScriptedAgentProvider();
        provider.Add((_, _, _) => { workspace.Edit("View.axaml", "after"); return Task.FromResult(ScriptedAgentProvider.Done()); });
        provider.Add(ScriptedAgentProvider.Done()); provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(new AutomationCatalog(), workspace);
        var first = harness.CreateTask("First", provider, "fixture", Token);
        var second = harness.CreateTask("Second", provider, "fixture", Token);
        await harness.RunAsync(first.Id, "Edit", new(), cancellationToken: Token);
        var previous = harness.GetChangeReview(first.Id, latestRun: true);
        await harness.RunAsync(second.Id, "Inspect", new(), cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RestoreChangesAsync(second.Id, ["View.axaml"], previous.Revision, Token, expectedReviewId: previous.ReviewId));
        await harness.RunAsync(first.Id, "Inspect again", new(), cancellationToken: Token);
        var latest = harness.GetChangeReview(first.Id, latestRun: true);
        Assert.Equal(previous.Revision, latest.Revision); Assert.Empty(latest.Files); Assert.NotEqual(previous.ReviewId, latest.ReviewId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RestoreChangesAsync(first.Id, ["View.axaml"], previous.Revision, Token, latestRun: true, expectedReviewId: previous.ReviewId));
        Assert.Single(harness.GetChangeReview(first.Id).Files); Assert.Empty(workspace.Restores);
    }

    [Fact]
    public async Task Workspace_replacement_rejects_refresh_restore_run_and_compaction()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var workspace = new AgentTestWorkspace(); workspace.Edit("View.axaml", "before");
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(new AutomationCatalog(), workspace);
        var task = harness.CreateTask("Old workspace", provider, "fixture", lifetime.Token);
        await harness.RunAsync(task.Id, "Inspect", new(), cancellationToken: Token);
        var review = harness.GetChangeReview(task.Id); lifetime.Cancel();
        Assert.True(task.IsPreviousWorkspace);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RefreshChangesAsync(task.Id, cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RestoreChangesAsync(task.Id, ["View.axaml"], review.Revision, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CompactAsync(task.Id, new(), Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(task.Id, "Edit", new(), cancellationToken: Token));
        Assert.Single(provider.Requests); Assert.Empty(workspace.Restores); Assert.Same(review, task.Changes);
    }

    [Fact]
    public async Task Cancellation_during_refresh_does_not_publish_a_late_workspace_snapshot()
    {
        var workspace = new AgentTestWorkspace(); workspace.Edit("View.axaml", "before");
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(new AutomationCatalog(), workspace);
        var task = harness.CreateTask("Refresh", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Inspect", new(), cancellationToken: Token);
        var review = task.Changes;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.BeforeCapture = async _ => { entered.SetResult(); await release.Task.WaitAsync(Token); };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresh = harness.RefreshChangesAsync(task.Id, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        workspace.Edit("View.axaml", "later"); cancellation.Cancel(); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Same(review, task.Changes);
        workspace.BeforeCapture = null;
        Assert.Single((await harness.RefreshChangesAsync(task.Id, cancellationToken: Token)).Files);
    }

    [Fact]
    public async Task Source_preparation_serializes_runs_and_rechecks_queued_text_before_acceptance()
    {
        var workspace = new AgentTestWorkspace(); workspace.Edit("View.axaml", "before");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.BeforeCapture = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(new AutomationCatalog(), workspace);
        var task = harness.CreateTask("Preparing", provider, "fixture", Token);
        var other = harness.CreateTask("Other", provider, "fixture", Token);
        harness.QueueMessage(task.Id, "Originally reviewed"); var queue = task.Queue;
        var run = harness.RunQueuedAsync(task.Id, queue.Messages[0].Id, queue.Revision, new(), cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(AgentTaskStatus.Preparing, task.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(other.Id, "Competing run", new(), cancellationToken: Token));
        Assert.Throws<InvalidOperationException>(() => harness.DeleteTask(task.Id));
        harness.EditQueuedMessage(task.Id, queue.Messages[0].Id, "Changed during capture", queue.Revision);
        release.SetResult(); await Assert.ThrowsAsync<AutomationException>(() => run);
        Assert.Empty(provider.Requests); Assert.Equal(AgentTaskStatus.Ready, task.Status);
        Assert.Equal("Changed during capture", Assert.Single(task.Queue.Messages).Text);
        workspace.BeforeCapture = null;
        await harness.RunQueuedAsync(task.Id, task.Queue.Messages[0].Id, task.Queue.Revision, new(), cancellationToken: Token);
        Assert.Equal("Changed during capture", Assert.Single(Assert.Single(provider.Requests).Messages).Text);
        Assert.Empty(task.Queue.Messages);
    }
}
