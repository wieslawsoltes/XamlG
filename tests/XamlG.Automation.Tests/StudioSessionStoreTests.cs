using XamlG.Agents;
using XamlG.Studio.Host;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class StudioSessionStoreTests
{
    [Fact]
    public async Task Tokens_and_sessions_survive_restart_and_the_previous_complete_write_recovers_corruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xamlg-store-test-" + Guid.NewGuid().ToString("N"));
        var tokenName = "XAMLG_SYNTHETIC_TEST_" + Guid.NewGuid().ToString("N");
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            string token;
            using (var store = new StudioSessionStore(directory))
            {
                token = await store.TokenAsync(tokenName);
                await store.SaveAsync(new(1, []), cancellationToken);
                await store.SaveAsync(new(1, [new() { Id = "saved-task", Name = "Remembered", Provider = "fixture", Model = "fixture" }]), cancellationToken);
                Assert.Throws<IOException>(() => new StudioSessionStore(directory));
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "session.dat")));
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
                }
            }
            using (var restarted = new StudioSessionStore(directory))
            {
                Assert.Equal(token, await restarted.TokenAsync(tokenName));
                Assert.Equal("saved-task", Assert.Single((await restarted.LoadAsync(cancellationToken))!.Tasks).Id);
                await File.WriteAllBytesAsync(Path.Combine(directory, "session.dat"), [0xff, 0xff, 0xff], cancellationToken);
                Assert.Empty((await restarted.LoadAsync(cancellationToken))!.Tasks);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
