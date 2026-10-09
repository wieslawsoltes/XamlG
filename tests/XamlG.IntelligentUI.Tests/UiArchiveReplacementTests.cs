using XamlG.Automation;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiArchiveReplacementTests
{
    [Fact]
    public void OwnerRestoreIsAtomicAndInvalidatesOldRevisions()
    {
        var store = new UiSessionStore(); var original = store.Publish(UiExamples.Pricing("pricing"), "owner");
        var archive = store.CaptureArchive("workspace"); var generation = store.Generation;
        store.ReplaceArchive(archive, "workspace", generation);
        var restored = store.Read("pricing", "owner");
        Assert.Equal(original.SessionId, restored.SessionId); Assert.True(restored.Revision > original.Revision);
        Assert.Throws<UiException>(() => store.ReplaceArchive(archive, "workspace", generation));
        Assert.Throws<UiException>(() => store.ChangeState(new("pricing", original.Revision, original.StateRevision, "seats", AutomationJson.Element(12)), "owner"));
        var current = store.Read("pricing", "owner");
        Assert.Throws<UiException>(() => store.ReplaceArchive("{}", "workspace", store.Generation));
        Assert.Same(current, store.Read("pricing", "owner"));
    }
}
