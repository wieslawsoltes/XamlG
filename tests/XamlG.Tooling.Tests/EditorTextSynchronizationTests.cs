using Microsoft.AspNetCore.Components;
using XamlG.Playground.Editing;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class EditorTextSynchronizationTests
{
    [Fact]
    public async Task CapturingPendingTextDoesNotReinterpretUnchangedParametersAsAnExternalEdit()
    {
        var js = new EditorInteropFixture();
        await using var owner = new EditorInteropModule(js);
        await using var editor = new TestCodeEditor(owner, "original", "Resource.axaml");
        await editor.ParametersAsync(); await editor.RenderAsync();
        js.Buffers[1] = "pending before debounce";
        Assert.Equal("pending before debounce", await editor.GetTextAsync());
        // A render can replay the still-unacknowledged source parameter while capture
        // completes. That is not an instruction to replace the browser buffer.
        await editor.ParametersAsync();
        Assert.Equal("pending before debounce", js.Buffers[1]);
    }

    [Fact]
    public async Task AcknowledgingCapturedTextDoesNotOverwriteTypingAfterTheCapture()
    {
        var js = new EditorInteropFixture();
        await using var owner = new EditorInteropModule(js);
        await using var editor = new TestCodeEditor(owner, "original", "Resource.axaml");
        await editor.ParametersAsync(); await editor.RenderAsync();
        js.Buffers[1] = "captured";
        Assert.Equal("captured", await editor.GetTextAsync());
        js.Buffers[1] = "typed after capture";
        await editor.ParametersAsync(new Dictionary<string, object?> { [nameof(editor.Text)] = "captured" });
        Assert.Equal("typed after capture", js.Buffers[1]);
    }

    [Fact]
    public async Task CallbackAndParameterAcknowledgementDoNotReplaceANewerBrowserBuffer()
    {
        var js = new EditorInteropFixture();
        await using var owner = new EditorInteropModule(js);
        await using var editor = new TestCodeEditor(owner, "original", "Resource.axaml");
        await editor.ParametersAsync(new Dictionary<string, object?>
        { [nameof(editor.TextChanged)] = EventCallback.Factory.Create<string>(new object(), _ => Task.CompletedTask) });
        await editor.RenderAsync();
        js.Buffers[1] = "published";
        await editor.Changed("published");
        js.Buffers[1] = "newer pending input";
        await editor.ParametersAsync();
        await editor.ParametersAsync(new Dictionary<string, object?> { [nameof(editor.Text)] = "published" });
        Assert.Equal("newer pending input", js.Buffers[1]);
    }

    [Fact]
    public async Task ARealSourceReplacementIsAppliedAfterCapturedTextIsAcknowledged()
    {
        var js = new EditorInteropFixture();
        await using var owner = new EditorInteropModule(js);
        await using var editor = new TestCodeEditor(owner, "original", "Resource.axaml");
        await editor.ParametersAsync(); await editor.RenderAsync();
        js.Buffers[1] = "edited";
        await editor.GetTextAsync();
        await editor.ParametersAsync(new Dictionary<string, object?> { [nameof(editor.Text)] = "edited" });
        await editor.ParametersAsync(new Dictionary<string, object?> { [nameof(editor.Text)] = "original" });
        Assert.Equal("original", js.Buffers[1]);
    }
}
