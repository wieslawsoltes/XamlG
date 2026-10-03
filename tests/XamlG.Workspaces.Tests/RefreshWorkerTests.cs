using System.Collections.Concurrent;
using XamlG.Workspaces.Watching;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class RefreshWorkerTests
{
    [Fact]
    public async Task ALoaderIgnoringCancellationCannotPublishItsSupersededResult()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<RevisionedValue<long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new ConcurrentQueue<long>();
        await using var worker = new LatestRevisionWorker<long>(async (revision, token) =>
        {
            if (revision == 1) { firstStarted.SetResult(); await releaseFirst.Task; }
            return revision;
        }, value => { results.Enqueue(value.Revision); published.TrySetResult(value); }, debounce: TimeSpan.Zero);
        worker.Signal(); await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Signal(); releaseFirst.SetResult();
        var latest = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, latest.Revision); Assert.Equal(2, latest.Value);
        Assert.Equal(new[] { 2L }, results.ToArray());
    }

    [Fact]
    public async Task FailedRefreshDoesNotPreventTheNextRevision()
    {
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new LatestRevisionWorker<long>((revision, _) => revision == 1 ? Task.FromException<long>(new IOException("failed read")) : Task.FromResult(revision),
            value => result.TrySetResult(value.Value), _ => error.TrySetResult(), TimeSpan.Zero);
        worker.Signal(); await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, worker.PublishedRevision);
        worker.Signal(); Assert.Equal(2, await result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisposalCancelsInFlightWorkAndPreventsPublication()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = false;
        var worker = new LatestRevisionWorker<int>(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return 1; }, _ => published = true, debounce: TimeSpan.Zero);
        worker.Signal(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.DisposeAsync(); worker.Signal();
        Assert.False(published);
    }

    [Fact]
    public void WatchFilteringTracksInputsButIgnoresUnrelatedBuildOutputs()
    {
        var root = Path.GetFullPath("project");
        var assets = Path.Combine(root, "obj", "project.assets.json");
        var inputs = new XamlWatchInputs(new[] { assets }, new[] { root });
        Assert.True(inputs.Contains(Path.Combine(root, "New.cs")));
        Assert.True(inputs.Contains(Path.Combine(root, "Views", "View.axaml")));
        Assert.True(inputs.Contains(assets));
        Assert.False(inputs.Contains(Path.Combine(root, "obj", "Generated.cs")));
        Assert.False(inputs.Contains(Path.Combine(root, "bin", "Output.dll")));
        Assert.False(inputs.Contains(Path.Combine(root, "node_modules", "test.cs")));
    }
}
