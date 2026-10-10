#!/usr/bin/env python3
"""Temporary exact-source preparation for XamlG #23. Never updates a Git ref."""
import base64, hashlib, json, os, subprocess, sys, urllib.request
from pathlib import Path

EXPECTED = {'docs/solution-workspaces.md': '79ba7ddce8f5ba2fb6bda3fa0b9c4d3b07d43671f8dbcc262d48ef7a5df4e54a', 'tests/XamlG.Workspaces.Tests/WorkspaceCancellationTests.cs': 'a337611eb12ff038d5567e32c41a261155ec0f325a25576d18a2f46f87724a5f', 'tools/XamlG.Playground/Components/SolutionExplorer.razor': '53b5000e4282515d1648750e028ea64040d2ce8b9d4218e82b6806100f70a49d', 'tools/XamlG.Playground/Components/SolutionProjectWizard.razor': '901ed7723d2e9c98e812844f44b2ae17c34d5a3dd074639823e3a534bd582cf6', 'tools/XamlG.Playground/Components/WorkspaceOutput.razor': 'd2147fc57c031b3c5034adf467a72c3e42523f6d1b85ee322ba99f6b1dcf1a6e', 'tools/XamlG.Playground/Workspaces/SolutionWorkspaceSession.cs': '1f310825890fba4e20c8824f49d93bb1dc161087fe749b3ed77cd959bd790eeb', 'tools/XamlG.Playground/asset-tests/workspace-cancellation.test.mjs': '78fdfc53e23a13a10a3c4b724eec60208648ef40dc474db95ee2f4bd3525c182', 'tools/XamlG.Playground/tests/solution-workspace.spec.mjs': '7e4579325d8518ad99c8662434f5fb536c824f4c3d6a3527bd38782873123c8c', 'tools/XamlG.Playground/wwwroot/solution-workspace.js': 'da5d6f585cc9672a17c70d697922d89406c8ca2d79552b7922063812f2960536', 'tools/XamlG.Playground/wwwroot/studio.js': '5e1b7489f7ce4836601498c5d4d5ebcb0c4560f4ce80d696f2139cf550826be8'}
CLEANUP = ["scripts/prepare-workspace-cancellation.py", ".github/workflows/workspace-cancellation-prepare.yml"]
EXPECTED_TREE = "75d04a7c3530ed0df29ae32ac12c1af0f4849ee7"

def verify():
    for path, expected in EXPECTED.items():
        actual = hashlib.sha256(Path(path).read_bytes()).hexdigest()
        if actual != expected: raise RuntimeError(f"Prepared bytes differ: {path}: {actual}")

def git(*args):
    return subprocess.check_output(["git", *args], text=True).strip()

def api(path, body=None):
    url = "https://api.github.com/repos/wieslawsoltes/XamlG" + path
    payload = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(url, data=payload, headers={"Authorization": "Bearer " + os.environ["GH_TOKEN"], "Accept": "application/vnd.github+json", "Content-Type": "application/json", "X-GitHub-Api-Version": "2022-11-28"})
    with urllib.request.urlopen(request, timeout=60) as response: return json.load(response)

def publish():
    verify()
    parent = git("rev-parse", "HEAD")
    if api("/git/ref/heads/feat/msbuild-solution-workspaces")["object"]["sha"] != parent:
        raise RuntimeError("The PR branch changed. Reconcile before publishing; no ref is updated here.")
    subprocess.run(["git", "add", "--", *EXPECTED], check=True)
    subprocess.run(["git", "rm", "--", *CLEANUP], check=True)
    local_tree = git("write-tree")
    if local_tree != EXPECTED_TREE: raise RuntimeError("Unexpected candidate tree: " + local_tree)
    entries = []
    for path in EXPECTED:
        blob = api("/git/blobs", {"content": base64.b64encode(Path(path).read_bytes()).decode(), "encoding": "base64"})
        if blob["sha"] != git("hash-object", path): raise RuntimeError("Blob identity mismatch: " + path)
        entries.append({"path": path, "mode": "100644", "type": "blob", "sha": blob["sha"]})
    entries += [{"path": path, "mode": "100644", "type": "blob", "sha": None} for path in CLEANUP]
    tree = api("/git/trees", {"base_tree": git("rev-parse", "HEAD^{tree}"), "tree": entries})
    if tree["sha"] != local_tree: raise RuntimeError("Remote tree identity mismatch")
    commit = api("/git/commits", {"message": "Add scoped SDK cancellation across Explorer, output and creation wizard\n\nPreserve owner pairing and sibling requests, reject late canceled responses, release abort listeners and add native/browser regressions. Remove temporary source-preparation helpers.", "tree": tree["sha"], "parents": [parent]})
    Path("artifacts/workspace-cancellation").mkdir(parents=True, exist_ok=True)
    result = {"parent": parent, "commit": commit["sha"], "tree": tree["sha"], "files": EXPECTED, "ref_updated": False}
    Path("artifacts/workspace-cancellation/candidate.json").write_text(json.dumps(result, indent=2))
    print(json.dumps(result, indent=2))


if sys.argv[1:] == ["--publish"]:
    publish()
    raise SystemExit(0)
if sys.argv[1:] != ["--apply"]: raise SystemExit("Choose --apply or --publish.")
from pathlib import Path
root=Path.cwd()
def change(path,old,new):
 p=root/path;s=p.read_text();assert s.count(old)==1,(path,s.count(old));p.write_text(s.replace(old,new))
change('tools/XamlG.Playground/wwwroot/studio.js', '''export async function agentRequest(action, argumentsValue = {}) {
    if (!agentConnection) throw new Error('Connect the local companion in Agent access first.');
    const connection = agentConnection;
    const response = await fetch(`${connection.base}/agent/${encodeURIComponent(action)}`, {
        method: 'POST', headers: { Authorization: `Bearer ${connection.token}`, 'X-Xamlg-Owner-Session': connection.session, 'Content-Type': 'application/json' },
        body: JSON.stringify(argumentsValue), cache: 'no-store', signal: agentStream?.signal
    });
    const body = await response.text();
    if (body.length > 16 * 1024 * 1024) throw new Error('Agent response is too large. Export a smaller thread.');
    const result = JSON.parse(body);
    if (!response.ok) throw new Error(result.error || `Companion returned ${response.status}.`);
    return result;
}''', '''export async function agentRequest(action, argumentsValue = {}, { signal } = {}) {
    if (!agentConnection) throw new Error('Connect the local companion in Agent access first.');
    const connection = agentConnection;
    // An owner operation may cancel its HTTP request, never the shared stream or
    // another operation. Revoke/disconnect still cancels every linked request.
    const controller = new AbortController(), subscriptions = [];
    try {
        for (const source of new Set([agentStream?.signal, signal].filter(Boolean))) {
            const abort = () => controller.abort(source.reason);
            if (source.aborted) abort();
            else { source.addEventListener('abort', abort, { once: true }); subscriptions.push([source, abort]); }
        }
        controller.signal.throwIfAborted();
        const response = await fetch(`${connection.base}/agent/${encodeURIComponent(action)}`, {
            method: 'POST', headers: { Authorization: `Bearer ${connection.token}`, 'X-Xamlg-Owner-Session': connection.session, 'Content-Type': 'application/json' },
            body: JSON.stringify(argumentsValue), cache: 'no-store', signal: controller.signal
        });
        const body = await response.text();
        controller.signal.throwIfAborted();
        if (body.length > 16 * 1024 * 1024) throw new Error('Agent response is too large. Export a smaller thread.');
        const result = JSON.parse(body);
        if (!response.ok) throw new Error(result.error || `Companion returned ${response.status}.`);
        return result;
    } finally { for (const [source, abort] of subscriptions) source.removeEventListener('abort', abort); }
}''')
change('tools/XamlG.Playground/wwwroot/solution-workspace.js', '''export async function nativeRequest(action, args) {
  if (typeof action !== 'string' || !action.startsWith('workspace_')) throw new Error('Invalid workspace action.');
  const studio = await (globalThis.xamlgBoot?.importModule('studio.js') ?? import('./studio.js'));
  return studio.agentRequest(action, args);
}''', '''const pendingNativeRequests = new Map();
export async function nativeRequest(action, args, requestId = null) {
  if (typeof action !== 'string' || !/^workspace_[a-z_]+$/.test(action)) throw new Error('Invalid workspace action.');
  if (requestId !== null && (typeof requestId !== 'string' || !/^[a-zA-Z0-9-]{1,64}$/.test(requestId)))
    throw new Error('Invalid workspace request identity.');
  if (requestId !== null && pendingNativeRequests.has(requestId)) throw new Error('This workspace request is already running.');
  const controller = new AbortController();
  if (requestId !== null) pendingNativeRequests.set(requestId, controller);
  try {
    const studio = await (globalThis.xamlgBoot?.importModule('studio.js') ?? import('./studio.js'));
    controller.signal.throwIfAborted();
    const result = await studio.agentRequest(action, args, { signal: controller.signal });
    controller.signal.throwIfAborted();
    return result;
  } finally {
    if (requestId !== null && pendingNativeRequests.get(requestId) === controller) pendingNativeRequests.delete(requestId);
  }
}
export function cancelNativeRequest(requestId) {
  const controller = pendingNativeRequests.get(requestId);
  if (!controller || controller.signal.aborted) return false;
  controller.abort(new DOMException('Workspace operation cancelled. Completed filesystem effects are not rolled back.', 'AbortError'));
  return true;
}''')
change('tools/XamlG.Playground/Workspaces/SolutionWorkspaceSession.cs', '    private bool _disposed;\n', '''    private bool _disposed;
    private string? _nativeRequestId;
    private bool _cancelRequested;
''')
change('tools/XamlG.Playground/Workspaces/SolutionWorkspaceSession.cs', '    public bool Busy { get; private set; }\n', '''    public bool Busy { get; private set; }
    public bool CanCancel => _nativeRequestId != null && !_cancelRequested;
    public bool CancellationRequested => _nativeRequestId != null && _cancelRequested;
''')
change('tools/XamlG.Playground/Workspaces/SolutionWorkspaceSession.cs', '''        if (operation == "evaluate")
        {
            EvaluatedGraph = await NativeAsync<JsonElement>("workspace_evaluate",''', '''        EvaluatedGraph = null;
        Output = $"Running {operation} for {path}…";
        if (operation == "evaluate")
        {
            EvaluatedGraph = await CancellableNativeAsync<JsonElement>("workspace_evaluate",''')
for action in ['workspace_build', 'workspace_templates', 'workspace_template_help', 'workspace_template_install', 'workspace_create']:
 p=root/'tools/XamlG.Playground/Workspaces/SolutionWorkspaceSession.cs';s=p.read_text();old=f'NativeAsync<JsonElement>("{action}",';assert s.count(old)==1,action;p.write_text(s.replace(old,f'CancellableNativeAsync<JsonElement>("{action}",'))
change('tools/XamlG.Playground/Workspaces/SolutionWorkspaceSession.cs', '    private async Task<T> NativeAsync<T>(string action, object arguments) => await _module!.InvokeAsync<T>("nativeRequest", action, arguments);\n', '''    private async Task<T> NativeAsync<T>(string action, object arguments) => await _module!.InvokeAsync<T>("nativeRequest", action, arguments);

    public async Task CancelOperationAsync()
    {
        if (_disposed || !CanCancel || _module == null) return;
        var id = _nativeRequestId;
        _cancelRequested = true; Notify();
        try { await _module.InvokeAsync<bool>("cancelNativeRequest", id); }
        catch (JSException error) { Error = "Could not request cancellation: " + error.Message; }
        finally { Notify(); }
    }

    private async Task<T> CancellableNativeAsync<T>(string action, object arguments)
    {
        var id = Guid.NewGuid().ToString("N");
        _nativeRequestId = id; _cancelRequested = false; Notify();
        try { return await _module!.InvokeAsync<T>("nativeRequest", action, arguments, id); }
        catch (JSException error) when (_cancelRequested)
        { throw new OperationCanceledException("Workspace cancellation requested. Completed filesystem effects are not rolled back; refresh before retrying.", error); }
        finally
        {
            if (_nativeRequestId == id) { _nativeRequestId = null; _cancelRequested = false; }
            Notify();
        }
    }
''')
change('tools/XamlG.Playground/Workspaces/SolutionWorkspaceSession.cs', '''        try { await operation(); }
        catch (Exception error) { Error = error.Message; }''', '''        try { await operation(); }
        catch (OperationCanceledException error) { Output = error.Message; Error = error.Message; EvaluatedGraph = null; }
        catch (Exception error) { Error = error.Message; }''')
change('tools/XamlG.Playground/Workspaces/SolutionWorkspaceSession.cs', '''            try { await _module.InvokeVoidAsync("setUnsavedChanges", false); await _module.DisposeAsync(); }''', '''            try
            {
                if (_nativeRequestId != null) await _module.InvokeAsync<bool>("cancelNativeRequest", _nativeRequestId);
                await _module.InvokeVoidAsync("setUnsavedChanges", false); await _module.DisposeAsync();
            }''')
change('tools/XamlG.Playground/Components/WorkspaceOutput.razor', '''<small>@(Session.Busy ? "Operation running" : Session.IsLocal ? "Local SDK" : "Browser · structural inspection")</small></div>''', '''<small>@(Session.CancellationRequested ? "Cancellation requested" : Session.Busy ? "Operation running" : Session.IsLocal ? "Local SDK" : "Browser · structural inspection")</small>
        @if (Session.CanCancel || Session.CancellationRequested)
        { <button @onclick="Session.CancelOperationAsync" disabled="@(!Session.CanCancel)">Cancel operation</button> }
    </div>''')
change('tools/XamlG.Playground/Components/SolutionExplorer.razor', '''            <div class="ws-toolbar">@foreach (var operation in new[] { "restore", "build", "rebuild", "clean", "test", "evaluate" }) { <button @onclick="() => BuildAsync(operation)" disabled="@(Disabled || !Session.Trusted || Session.SdkTargetPath == null)">@operation</button> }</div>''', '''            <div class="ws-toolbar">@foreach (var operation in new[] { "restore", "build", "rebuild", "clean", "test", "evaluate" }) { <button @onclick="() => BuildAsync(operation)" disabled="@(Disabled || !Session.Trusted || Session.SdkTargetPath == null)">@operation</button> }
                @if (Session.CanCancel || Session.CancellationRequested)
                { <button @onclick="Session.CancelOperationAsync" disabled="@(!Session.CanCancel)">Cancel operation</button> }
            </div>''')

change("tools/XamlG.Playground/Components/SolutionProjectWizard.razor", '<footer><span>@(Session.Busy ? "Workspace operation running…" : "Existing files are never replaced by this wizard.")</span>', '<footer><span>@(Session.CancellationRequested ? "Cancellation requested…" : Session.Busy ? "Workspace operation running…" : "Existing files are never replaced by this wizard.")</span>\n                @if (Session.CanCancel || Session.CancellationRequested)\n                { <button @onclick="Session.CancelOperationAsync" disabled="@(!Session.CanCancel)">Cancel operation</button> }\n                ')

change("tools/XamlG.Playground/tests/solution-workspace.spec.mjs", "  const output = page.getByRole('region', { name: 'Workspace output', exact: true });\n", "  const output = page.getByRole('region', { name: 'Workspace output', exact: true });\n  // Hold exactly one browser request to exercise the production C#/JS Cancel\n  // control deterministically. The subsequent SDK operations still use the real\n  // companion, proving that cancellation did not revoke the paired connection.\n  await page.evaluate(() => {\n    const original = window.fetch;\n    window.workspaceCancellationFetch = original;\n    window.fetch = function (address, options) {\n      if (String(address).endsWith('/agent/workspace_build')) {\n        window.fetch = original;\n        window.workspaceCancellationStarted = true;\n        return new Promise((_resolve, reject) => {\n          if (!options?.signal) { reject(new Error('Missing per-operation cancellation signal')); return; }\n          if (options.signal.aborted) { reject(options.signal.reason); return; }\n          options.signal.addEventListener('abort', () => {\n            window.workspaceCancellationObserved = true;\n            reject(options.signal.reason);\n          }, { once: true });\n        });\n      }\n      return original.call(this, address, options);\n    };\n  });\n  try {\n    await pane.getByRole('button', { name: 'build', exact: true }).click();\n    await expect.poll(() => page.evaluate(() => window.workspaceCancellationStarted)).toBe(true);\n    await expect(output.getByRole('button', { name: 'Cancel operation', exact: true })).toBeEnabled();\n    await output.getByRole('button', { name: 'Cancel operation', exact: true }).click();\n    await expect.poll(() => page.evaluate(() => window.workspaceCancellationObserved)).toBe(true);\n    await expect(pane.getByRole('tree')).toHaveAttribute('aria-busy', 'false');\n    await expect(output.getByRole('log')).toContainText('not rolled back');\n    expect((await api(page, 'workspace_status')).enabled).toBe(true);\n  } finally {\n    await page.evaluate(() => {\n      window.fetch = window.workspaceCancellationFetch;\n      delete window.workspaceCancellationFetch;\n    });\n  }\n")

p=root/"docs/solution-workspaces.md"; p.write_text(p.read_text()+'\n## Cancelling trusted SDK operations\n\nThe Explorer, workspace output pane and project wizard expose **Cancel operation**\nwhile restore/build/rebuild/clean/test, Roslyn evaluation or installed-template\noperations are waiting for the companion. Cancellation aborts only that HTTP\nrequest. It does not revoke the paired owner session, disconnect the coding-agent\nstream, cancel a sibling request, or discard editor buffers. Session revocation\nstill cancels all requests linked to that owner. The existing companion request\ncancellation token flows through the serialized SDK service to the process runner.\n\nCancellation is not rollback: a build, restore, package installation or template\nmay already have changed files. The UI makes that boundary explicit and does not\nautomatically repeat an interrupted operation or treat cancelled creation as a\nsuccess. Local file saves are completed before starting the cancellable SDK\nrequest. Browser tests exercise the real Cancel control with a held HTTP request,\nthen perform actual companion restore/build/evaluation on the same connection.\nNative tests verify running/queued cancellation and release of the SDK operation\nlease. Neither fixture represents an OS-level process-termination measurement.\n')

verify()
