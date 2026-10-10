using XamlG.Workspaces.Studio;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class WorkspaceCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XamlG-cancel-" + Guid.NewGuid().ToString("N"));
    public WorkspaceCancellationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Cancelling_a_running_sdk_request_releases_the_serialized_workspace()
    {
        var runner = new BlockingRunner();
        var workspace = new StudioWorkspaceService(_root, runner);
        using var cancellation = new CancellationTokenSource();
        var running = workspace.TemplatesAsync(true, cancellation.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var next = await workspace.TemplateHelpAsync("console", true).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, next.ExitCode);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task Cancelling_a_queued_request_does_not_cancel_the_active_owner_request()
    {
        var runner = new BlockingRunner();
        var workspace = new StudioWorkspaceService(_root, runner);
        var running = workspace.TemplatesAsync(true);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var queued = workspace.TemplateHelpAsync("console", true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, runner.Calls);
        Assert.False(running.IsCompleted);
        runner.Release.TrySetResult();
        Assert.Equal(0, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Command.ExitCode);
        Assert.Equal(0, (await workspace.TemplateHelpAsync("console", true)).ExitCode);
        Assert.Equal(2, runner.Calls);
    }

    private sealed class BlockingRunner : ISdkProcessRunner
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SdkCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new(0, "Completed", "", false);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
