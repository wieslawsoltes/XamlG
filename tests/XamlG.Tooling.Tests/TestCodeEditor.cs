using XamlG.Playground.Components;

namespace XamlG.Tooling.Tests;

internal sealed class TestCodeEditor : CodeEditor
{
    public Task RenderAsync() => OnAfterRenderAsync(true);
    public Task ParametersAsync() => OnParametersSetAsync();
}
