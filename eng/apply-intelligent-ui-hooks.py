"""One-shot, exact-match integration patch for PR 18. Remove after the resulting source commit.

The local execution sandbox became unavailable. This applies the already-reviewed small
hooks to existing large files without regenerating or reformatting those files. All matches
are checked before any write; no downloaded code, credentials or arbitrary commands are used.
"""
from pathlib import Path

updates = {}
def change(path, old, new):
    text = updates.get(path, Path(path).read_text())
    if text.count(old) != 1:
        raise RuntimeError(f"Expected exactly one unchanged integration anchor in {path}: {old[:100]!r}")
    updates[path] = text.replace(old, new, 1)

change('tools/XamlG.Playground/App.Automation.cs',
       '        _browserAgents = new(new BrowserAgentHost(this));',
       '        InitializeIntelligentUi();\n        _browserAgents = new(new BrowserAgentHost(this));')
change('tools/XamlG.Playground/App.Automation.cs',
       '        await _automationGate.WaitAsync();\n        _automationGate.Release();',
       '        await _automationGate.WaitAsync();\n        try { _intelligentUi.Clear(); } finally { _automationGate.Release(); }')
change('tools/XamlG.Playground/App.razor.cs',
       '        _browserAgents.Dispose();',
       '        _browserAgents.Dispose();\n        DisposeIntelligentUi();')
change('tools/XamlG.Playground/App.razor',
       '<link rel="stylesheet" href="agent-workbench.css" />',
       '<link rel="stylesheet" href="agent-workbench.css" />\n<link rel="stylesheet" href="intelligent-ui.css" />')
change('tools/XamlG.Playground/App.razor',
       '<AgentWorkbench @ref="_agentWorkbench" BrowserRuntime="_browserAgents"',
       '<AgentWorkbench @ref="_agentWorkbench" UiStore="_intelligentUi" UiReady="_ready" UiTools="_automation.Tools" UiToolRequested="ExecuteIntelligentUiToolAsync" BrowserRuntime="_browserAgents"')
change('tools/XamlG.Playground/Components/AgentWorkbench.razor',
       '    </nav>\n',
       '''    </nav>
    @if (UiStore != null)
    {
        <details class="intelligent-ui-demo"><summary>Intelligent UI</summary>
            <p>Reactive Avalonia XAML and C# expressions. This example runs locally and needs no provider key.</p>
            <button @onclick="ShowIntelligentUiDemo" disabled="@(!UiReady)">Try intelligent UI</button>
            @if (_uiDemo != null)
            { <IntelligentUiCard Store="UiStore" Presentation="_uiDemo" Ready="UiReady" MessageRequested="UseUiMessageAsync" ToolRequested="UiToolRequested" Tools="UiTools" /> }
        </details>
    }
''')
old = '                        <AgentEventCard @key="item.Sequence" Sequence="item.Sequence" Sequences="@entry.Sequences" Kind="@result.Kind" Text="@result.Text" ResultPreview="result.ResultPreview" Images="result.Images" ToolName="@(item.ToolName ?? (item.Kind == "tool_started" ? item.Text : result.ToolName))" Running="@IsRunning(selected)" CopyRequested="CopyTextAsync" />'
change('tools/XamlG.Playground/Components/AgentWorkbench.razor', old,
       '''                        @if (UiStore != null && XamlG.IntelligentUI.UiPresentation.TryRead(result.Text, out var presentation))
                        { <IntelligentUiCard @key="item.Sequence" Store="UiStore" Presentation="presentation!" Ready="UiReady" MessageRequested="UseUiMessageAsync" ToolRequested="UiToolRequested" Tools="UiTools" /> }
                        else
                        {
''' + old + '\n                        }')
change('src/XamlG.Agents/AgentHarness.cs',
       'result = await target.CallAsync(tool.Name, call.Arguments, new("AI Agent", lease.Token));',
       'result = await target.CallAsync(tool.Name, call.Arguments, new("AI Agent", lease.Token, "agent:" + task.Id));')
change('tools/XamlG.Studio.Host/Program.cs', '.WithAutomation(bridge)', '.WithAutomation(bridge).WithAutomationUi()')
change('tests/XamlG.IntelligentUI.Tests/UiIntegrationTests.cs',
       'Assert.Empty(CSharpSyntaxTree.ParseText(source).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));',
       'Assert.DoesNotContain(CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken).GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == DiagnosticSeverity.Error);')

for name, text in updates.items():
    Path(name).write_text(text)
    print('Integrated:', name)
