using Microsoft.AspNetCore.Components;
using XamlG.Playground.Components;
using XamlG.Playground.Editing;

namespace XamlG.Tooling.Tests;

/// <summary>Drives the real component's lifecycle with Blazor's parameter assignment mechanism,
/// without a renderer or WebAssembly toolchain. Production JS behavior is tested separately in Playwright.</summary>
internal sealed class TestCodeEditor : CodeEditor
{
    public TestCodeEditor(EditorInteropModule editors, string text = "source", string? path = null)
    {
        Editors = editors;
        ParameterView.FromDictionary(new Dictionary<string, object?>
        { [nameof(Text)] = text, [nameof(DocumentPath)] = path }).SetParameterProperties(this);
    }
    public Task RenderAsync() => OnAfterRenderAsync(true);
    public Task ParametersAsync(IDictionary<string, object?>? parameters = null)
    {
        if (parameters != null) ParameterView.FromDictionary(parameters).SetParameterProperties(this);
        return OnParametersSetAsync();
    }
}
