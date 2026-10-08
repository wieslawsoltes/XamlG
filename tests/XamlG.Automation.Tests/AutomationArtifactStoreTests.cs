using System.Security.Cryptography;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AutomationArtifactStoreTests
{
    [Fact]
    public void Artifacts_retain_exact_bytes_and_revisions_and_enforce_principal_ownership_for_reads_and_release()
    {
        using var store = new AutomationArtifactStore();
        var source = Enumerable.Range(0, 300000).Select(index => (byte)index).ToArray();
        var expected = source.ToArray();
        var artifact = store.Add("owner", "source.zip", "application/zip", 42, source);
        Array.Fill(source, (byte)0);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), artifact.Sha256);
        Assert.Equal(42, artifact.Revision);
        Assert.Equal(TimeSpan.FromMinutes(5), artifact.ExpiresAt - artifact.CreatedAt);
        var first = store.Read(artifact.Id, "owner");
        var last = store.Read(artifact.Id, "owner", first.Count);
        Assert.True(first.HasMore); Assert.False(last.HasMore);
        Assert.Equal(expected, Convert.FromBase64String(first.Base64).Concat(Convert.FromBase64String(last.Base64)).ToArray());
        Assert.Equal(0, store.Read(artifact.Id, "owner", expected.Length).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Read(artifact.Id, "owner", 0, 262145));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Read(artifact.Id, "owner", expected.Length + 1));
        Assert.Equal("unknown_artifact", Assert.Throws<AutomationException>(() => store.Read(artifact.Id, "other")).Code);
        Assert.Throws<AutomationException>(() => store.Release(artifact.Id, "other"));
        Assert.Equal(expected, store.ReadLocal(artifact.Id));
        var localCopy = store.ReadLocal(artifact.Id); Array.Fill(localCopy, (byte)0);
        Assert.Equal(expected, store.ReadLocal(artifact.Id));
        store.Release(artifact.Id, "owner");
        Assert.Empty(store.LocalInventory);
        Assert.Throws<AutomationException>(() => store.Read(artifact.Id, "owner"));
    }

    [Fact]
    public void Expiry_is_absolute_and_not_extended_by_reads_and_clear_retires_every_workspace_handle()
    {
        var time = new ArtifactClock();
        using var store = new AutomationArtifactStore(time);
        var changed = new List<string>(); store.Changed += changed.Add;
        var first = store.Add("one", "one", "application/json", 1, [1, 2]);
        time.Advance(TimeSpan.FromMinutes(4));
        var second = store.Add("two", "two", "application/json", 2, [3, 4]);
        Assert.Equal(2, store.Read(first.Id, "one").Count);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<AutomationException>(() => store.Read(first.Id, "one"));
        Assert.Equal(second.Id, Assert.Single(store.LocalInventory).Id);
        Assert.Equal(new[] { first.Id, second.Id, first.Id }, changed);
        store.Clear(); Assert.Empty(store.LocalInventory);
        Assert.Equal(second.Id, changed[^1]);
        Assert.Throws<AutomationException>(() => store.Read(second.Id, "two"));
    }

    [Fact]
    public void Artifact_count_and_byte_limits_retire_oldest_handles_without_corrupting_retained_snapshots()
    {
        var time = new ArtifactClock();
        using var store = new AutomationArtifactStore(time);
        var ids = new List<string>();
        for (var index = 0; index < 9; index++)
        {
            ids.Add(store.Add("owner", "part", "application/octet-stream", index, [(byte)index]).Id);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(8, store.LocalInventory.Count);
        Assert.Throws<AutomationException>(() => store.Read(ids[0], "owner"));
        Assert.Equal(new byte[] { 8 }, Convert.FromBase64String(store.Read(ids[^1], "owner").Base64));
        store.Clear();
        var data = new byte[17 * 1024 * 1024]; data[0] = 42;
        var first = store.Add("owner", "first", "application/octet-stream", 1, data);
        time.Advance(TimeSpan.FromSeconds(1));
        var last = store.Add("owner", "last", "application/octet-stream", 2, data);
        Assert.Equal(last.Id, Assert.Single(store.LocalInventory).Id);
        Assert.Throws<AutomationException>(() => store.Read(first.Id, "owner"));
        Assert.Equal(new byte[] { 42 }, Convert.FromBase64String(store.Read(last.Id, "owner", count: 1).Base64));
    }

    private sealed class ArtifactClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
