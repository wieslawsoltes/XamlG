using XamlG.Playground.Editing;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class DialogFocusStateTests
{
    [Fact]
    public void OpeningAndFinishingValidationRestoreFocusWithoutStealingItDuringTyping()
    {
        var state = new DialogFocusState();
        Assert.False(state.Observe(false, false));
        Assert.True(state.Observe(true, false));
        Assert.False(state.Observe(true, false));
        Assert.False(state.Observe(true, true));
        Assert.False(state.Observe(true, true));
        Assert.True(state.Observe(true, false));
        Assert.False(state.Observe(true, false));
    }

    [Fact]
    public void ClosingDuringValidationNeverFocusesTheDetachedInput()
    {
        var state = new DialogFocusState();
        Assert.True(state.Observe(true, false));
        Assert.False(state.Observe(true, true));
        Assert.False(state.Observe(false, false));
        Assert.False(state.Observe(false, false));
        Assert.True(state.Observe(true, false));
    }

    [Fact]
    public void InitiallyBusyDialogsWaitForTheirInputToBeEnabled()
    {
        var state = new DialogFocusState();
        Assert.False(state.Observe(true, true));
        Assert.True(state.Observe(true, false));
        Assert.False(state.Observe(true, false));
    }
}
