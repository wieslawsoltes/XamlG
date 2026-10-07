import { installCSharpLanguage } from './csharp-language.js';
let monacoPromise;
const editors = new Map();
let sequence = 0;

function loadMonaco() {
  return monacoPromise ??= new Promise((resolve, reject) => {
    const base = new URL('monaco/vs/', document.baseURI).href;
    self.MonacoEnvironment = {
      getWorkerUrl() {
        const code = `self.MonacoEnvironment={baseUrl:${JSON.stringify(base)}};importScripts(${JSON.stringify(base + 'base/worker/workerMain.js')});`;
        return 'data:text/javascript;charset=utf-8,' + encodeURIComponent(code);
      }
    };
    const script = document.createElement('script');
    script.src = base + 'loader.js';
    script.onerror = () => reject(new Error('The local Monaco assets could not be loaded.'));
    script.onload = () => {
      self.require.config({ paths: { vs: base.slice(0, -1) } });
      self.require(['vs/editor/editor.main'], () => resolve(self.monaco), reject);
    };
    document.head.appendChild(script);
  });
}

// Each application scope owns a distinct JS reference. Releasing it cannot invalidate an
// unrelated component's import reference; editor IDs still share the module's monotonic registry.
export function createEditorInterop() {
  return { createEditor, getEditorText, setEditorText, reveal, setMarkers, disposeEditor, getAuthoringRequest };
}

export async function createEditor(host, dotnet, text, language, readOnly, path = null) {
  const id = ++sequence;
  try {
    const monaco = await loadMonaco();
    installCSharpLanguage(monaco, editors);
    const model = monaco.editor.createModel(text, language, path ? monaco.Uri.from({ scheme: 'xamlg', authority: 'studio', path: '/' + id + '/' + path }) : undefined);
    host.dataset.documentPath = path ?? '';
    const editor = monaco.editor.create(host, {
      model, readOnly, automaticLayout: true, theme: document.documentElement.dataset.theme === 'light' ? 'vs' : 'vs-dark',
      minimap: { enabled: false }, fontSize: 13, lineHeight: 21, padding: { top: 14 },
      scrollBeyondLastLine: false, roundedSelection: false, tabSize: 2, wordWrap: 'off',
      renderLineHighlight: 'line', smoothScrolling: true, bracketPairColorization: { enabled: true }
    });
    let timer;
    let applying = false;
    const subscription = editor.onDidChangeModelContent(() => {
      if (applying || readOnly) return;
      clearTimeout(timer);
      timer = setTimeout(() => {
        if (editors.has(id)) return dotnet.invokeMethodAsync('Changed', editor.getValue());
      }, 120);
    });
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => dotnet.invokeMethodAsync('Run'));
    if (path && !readOnly) {
      const actions = [
        ['rename', 'XamlG: Rename symbol', monaco.KeyCode.F2],
        ['format', 'XamlG: Format source', monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyF],
        ['actions', 'XamlG: Source actions', monaco.KeyMod.CtrlCmd | monaco.KeyCode.Period],
        ['undo', 'XamlG: Undo project edit', monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyZ],
        ['redo', 'XamlG: Redo project edit', monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyZ]
      ];
      for (const [command, label, keybinding] of actions) editor.addAction({
        id: 'xamlg.' + command, label, keybindings: [keybinding], contextMenuGroupId: 'xamlg',
        run: () => requestAuthoring(id, command)
      });
    }
    editors.set(id, { editor, model, subscription, path, dotnet, set: value => { applying = true; try { editor.setValue(value); } finally { applying = false; } }, cleanup: () => clearTimeout(timer) });
  } catch (error) {
    const textarea = document.createElement('textarea');
    textarea.className = 'editor-fallback'; textarea.value = text; textarea.readOnly = readOnly;
    textarea.setAttribute('aria-label', language + ' source editor');
    textarea.oninput = () => dotnet.invokeMethodAsync('Changed', textarea.value);
    host.replaceChildren(textarea);
    host.dataset.documentPath = path ?? '';
    editors.set(id, { textarea, path, dotnet });
    console.warn(error.message);
  }
  return id;
}

export function getEditorText(id) {
  const item = editors.get(id);
  if (!item) throw new Error('The source editor has been disposed.');
  item.cleanup?.();
  return item.editor ? item.editor.getValue() : item.textarea.value;
}
export function setEditorText(id, value) {
  const item = editors.get(id);
  if (!item) return;
  item.cleanup?.();
  if (item.editor && item.editor.getValue() !== value) item.set(value);
  if (item.textarea && item.textarea.value !== value) item.textarea.value = value;
}
export function reveal(id, start, length) {
  const item = editors.get(id);
  if (item?.editor) {
    const from = item.model.getPositionAt(start), to = item.model.getPositionAt(start + length);
    item.editor.setSelection(new self.monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column));
    item.editor.revealPositionInCenter(from); item.editor.focus();
  } else if (item?.textarea) { item.textarea.focus(); item.textarea.setSelectionRange(start, start + length); }
}
export function setMarkers(id, diagnostics) {
  const item = editors.get(id);
  if (!item?.model) return;
  self.monaco.editor.setModelMarkers(item.model, 'xamlg', diagnostics.map(d => ({
    code: d.code, message: d.message, severity: d.severity === 'Error' ? 8 : 4,
    startLineNumber: d.startLine, startColumn: d.startColumn, endLineNumber: d.endLine, endColumn: d.endLine === d.startLine ? Math.max(d.endColumn, d.startColumn + 1) : d.endColumn
  })));
}
export function disposeEditor(id) {
  const item = editors.get(id);
  // Stop dispatching callbacks before disposing models or the managed callback reference.
  editors.delete(id);
  item?.cleanup?.(); item?.subscription?.dispose(); item?.editor?.dispose(); item?.model?.dispose();
}
export function setTheme(theme) {
  document.documentElement.dataset.theme = theme;
  localStorage.setItem('xamlg.theme', theme);
  self.monaco?.editor.setTheme(theme === 'light' ? 'vs' : 'vs-dark');
}
export function loadTheme() { return localStorage.getItem('xamlg.theme') ?? 'dark'; }
export function saveDraft(xaml, code, resources = {}, codeFiles = {}) {
  localStorage.setItem('xamlg.draft', JSON.stringify({ version: 3, xaml, code, resources, codeFiles }));
}
export function loadDraft() {
  try {
    const source = localStorage.getItem('xamlg.draft');
    if (!source || source.length > 12 * 1024 * 1024) return null;
    const value = JSON.parse(source);
    return [1, 2, 3].includes(value?.version) && typeof value.xaml === 'string' && typeof value.code === 'string' ? value : null;
  } catch { return null; }
}
export function download(name, content, type = 'text/plain') {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// Synchronous data capture is distinct from managed callback dispatch. A managed caller can
// release its editor-operation gate before a command recursively captures all source buffers.
export function getAuthoringRequest(id, command) {
  const item = editors.get(id);
  if (!item?.path) return null;
  let start = 0, length = 0;
  if (item.editor) {
    const selection = item.editor.getSelection();
    start = item.model.getOffsetAt(selection.getStartPosition());
    length = item.model.getOffsetAt(selection.getEndPosition()) - start;
  } else { start = item.textarea.selectionStart; length = item.textarea.selectionEnd - start; }
  return { command, path: item.path, text: getEditorText(id), start, length };
}
export function requestAuthoring(id, command) {
  const item = editors.get(id);
  const request = getAuthoringRequest(id, command);
  if (item && request) return item.dotnet.invokeMethodAsync('Authoring', request);
}

export function waitForElement(id) {
    if (document.getElementById(id)) return Promise.resolve();
    return new Promise((resolve, reject) => {
        const observer = new MutationObserver(() => {
            if (document.getElementById(id)) { clearTimeout(timer); observer.disconnect(); resolve(); }
        });
        const timer = setTimeout(() => { observer.disconnect(); reject(new Error(`Pane ${id} did not mount.`)); }, 30000);
        observer.observe(document.body, { childList: true, subtree: true });
    });
}
export function saveDockyardLayout(layout) {
    try { localStorage.setItem('xamlg.dockyard.layout.v2', layout); } catch { }
}
export function loadDockyardLayout() {
    try { return localStorage.getItem('xamlg.dockyard.layout.v2'); } catch { return null; }
}

let automationOwner = null;
let automationSocket = null;
let agentConnection = null;
let agentOwner = null;
let agentOwnerId = null;
let agentStream = null;
export function installAutomation(owner) {
    automationOwner = owner;
    window.xamlgAutomation = Object.freeze({
        catalog: () => owner.invokeMethodAsync('AutomationCatalog'),
        call: (name, args = {}) => owner.invokeMethodAsync('AutomationInvoke', crypto.randomUUID(), 'call', name, args, 'Browser automation'),
        resource: uri => owner.invokeMethodAsync('AutomationInvoke', crypto.randomUUID(), 'resource', uri, {}, 'Browser automation')
    });
}
export async function connectAutomation(address, token) {
    const url = new URL(address);
    if (!['ws:', 'wss:'].includes(url.protocol) || url.username || url.password || url.search || url.hash)
        throw new Error('Use a WebSocket URL without credentials, query or fragment.');
    if (!['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname))
        throw new Error('The companion must run on loopback.');
    if (token.length < 32) throw new Error('Enter the companion’s owner token.');
    if (!automationOwner) throw new Error('The IDE is not ready.');
    disconnectAutomation();
    const catalog = await automationOwner.invokeMethodAsync('AutomationCatalog');
    const agentAccess = { base: url.origin.replace(/^ws/, 'http'), token };
    await new Promise((resolve, reject) => {
        const socket = new WebSocket(url); automationSocket = socket;
        const pending = new Set();
        let paired = false;
        const timeout = setTimeout(() => { socket.close(); reject(new Error('Companion pairing timed out.')); }, 10000);
        socket.onopen = () => { socket.send(JSON.stringify({ kind: 'hello', token, catalog })); token = ''; };
        socket.onerror = () => { clearTimeout(timeout); reject(new Error('Unable to connect to the companion.')); };
        socket.onclose = () => {
            clearTimeout(timeout);
            for (const id of pending) automationOwner?.invokeMethodAsync('AutomationCancel', id).catch(() => {});
            if (automationSocket === socket) { agentConnection = null; agentStream?.abort(); agentOwner?.invokeMethodAsync('AgentDisconnected').catch(() => {}); }
            if (!paired) reject(new Error('The companion rejected pairing.'));
        };
        socket.onmessage = async event => {
            if (automationSocket !== socket) { socket.close(); return; }
            if (typeof event.data !== 'string' || event.data.length > 8 * 1024 * 1024) { socket.close(); return; }
            let request;
            try { request = JSON.parse(event.data); } catch { socket.close(); return; }
            if (request.kind === 'ready') {
                if (paired || typeof request.ownerSession !== 'string' || request.ownerSession.length !== 64) { socket.close(); return; }
                paired = true; clearTimeout(timeout); agentAccess.session = request.ownerSession; agentConnection = agentAccess;
                startAgentStream(); agentOwner?.invokeMethodAsync('AgentRefresh').catch(() => {}); resolve(); return;
            }
            if (!paired) return;
            const id = `${agentAccess.session}:${request.id}`;
            if (request.kind === 'cancel') { await automationOwner.invokeMethodAsync('AutomationCancel', id); return; }
            if (request.kind !== 'request') return;
            if (pending.has(id)) { socket.close(); return; }
            pending.add(id);
            try {
                const result = await automationOwner.invokeMethodAsync('AutomationInvoke', id, request.method, request.name, request.arguments ?? {}, request.caller ?? 'MCP');
                if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: request.id, result }));
            } catch (error) {
                if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: request.id, error: { code: 'ide_error', message: String(error.message || error) } }));
            } finally { pending.delete(id); }
        };
    });
}
export function disconnectAutomation() {
    agentConnection = null; agentStream?.abort(); agentStream = null;
    if (automationSocket) { automationSocket.close(); automationSocket = null; }
    agentOwner?.invokeMethodAsync('AgentDisconnected').catch(() => {});
}

export function installAgentWorkbench(owner, id) { agentOwner = owner; agentOwnerId = id; }
export function uninstallAgentWorkbench(id) { if (agentOwnerId === id) { agentOwner = null; agentOwnerId = null; } }
export function agentConnected() { return !!agentConnection; }
export async function copyAgentText(text) { await navigator.clipboard.writeText(text); }
const agentThreadPositions = new Map();
const agentThreadBindings = new WeakMap();
export function bindAgentThread(element, taskId) {
    if (!element || agentThreadBindings.get(element)?.taskId === taskId) return;
    releaseAgentThread(element);
    let position = agentThreadPositions.get(taskId);
    if (!position) {
        position = { follow: true, top: 0, anchor: null, offset: 0 };
        agentThreadPositions.set(taskId, position);
        while (agentThreadPositions.size > 8) agentThreadPositions.delete(agentThreadPositions.keys().next().value);
    }
    function remember() {
        if (element.dataset.taskId !== taskId) return;
        position.top = element.scrollTop;
        position.follow = element.scrollHeight - element.scrollTop - element.clientHeight < 32;
        const top = element.getBoundingClientRect().top;
        const first = [...element.querySelectorAll('[data-sequence]')].find(item => item.getBoundingClientRect().bottom >= top);
        position.anchor = first?.dataset.sequence;
        position.offset = first ? first.getBoundingClientRect().top - top : 0;
    }
    function restore() {
        if (element.dataset.taskId !== taskId) return;
        const selection = window.getSelection();
        if (selection && !selection.isCollapsed && element.contains(selection.anchorNode)) return;
        if (position.follow) element.scrollTop = element.scrollHeight;
        else {
            const anchor = position.anchor && [...element.querySelectorAll('[data-sequence]')].find(item => item.dataset.sequence === position.anchor);
            element.scrollTop = anchor ? element.scrollTop + anchor.getBoundingClientRect().top - element.getBoundingClientRect().top - position.offset : position.top;
        }
    }
    const observer = new MutationObserver(restore);
    observer.observe(element, { childList: true, subtree: true, characterData: true });
    element.addEventListener('scroll', remember, { passive: true });
    agentThreadBindings.set(element, { taskId, observer, remember, position }); restore();
}
export function releaseAgentThread(element) {
    const binding = agentThreadBindings.get(element);
    if (binding) { binding.observer.disconnect(); element.removeEventListener('scroll', binding.remember); agentThreadBindings.delete(element); }
}
export function followAgentThread(element) {
    const binding = agentThreadBindings.get(element);
    if (binding) binding.position.follow = true;
    if (element) element.scrollTop = element.scrollHeight;
}
export function loadAgentNumericPreferences() {
    try { return JSON.parse(localStorage.getItem('xamlg.agent.numeric.v1') || 'null'); } catch { return null; }
}
export function saveAgentNumericPreferences(value) {
    // Explicit allowlist: no task text, connection settings, credentials or grants.
    const keys = ['requests', 'tools', 'outputTokens', 'taskTokens', 'contextBytes', 'toolResultBytes', 'retries', 'timeoutMinutes', 'leaseMinutes',
        'automaticInputTokens', 'modelContextWindowTokens', 'recentCompleteTurns', 'checkpointOutputTokens'];
    const saved = {};
    for (const key of keys) {
        if (!Number.isSafeInteger(value[key])) throw new Error('Numeric preferences must be whole numbers.');
        saved[key] = value[key];
    }
    localStorage.setItem('xamlg.agent.numeric.v1', JSON.stringify(saved));
}
export async function agentRequest(action, argumentsValue = {}) {
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
}
async function startAgentStream() {
    agentStream?.abort(); agentStream = new AbortController();
    const controller = agentStream, connection = agentConnection;
    if (!connection) return;
    try {
        const response = await fetch(`${connection.base}/agent/events`, {
            headers: { Authorization: `Bearer ${connection.token}`, 'X-Xamlg-Owner-Session': connection.session }, cache: 'no-store', signal: controller.signal
        });
        if (!response.ok) throw new Error(`Agent event stream returned ${response.status}.`);
        const reader = response.body.getReader(), decoder = new TextDecoder();
        let buffer = '';
        while (!controller.signal.aborted) {
            const { value, done } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });
            if (buffer.length > 8 * 1024 * 1024) throw new Error('Agent event exceeded the stream limit.');
            let end;
            while ((end = buffer.indexOf('\n\n')) >= 0) {
                const frame = buffer.slice(0, end); buffer = buffer.slice(end + 2);
                if (frame.startsWith('data: ') && agentOwner) await agentOwner.invokeMethodAsync('AgentStream', JSON.parse(frame.slice(6)));
            }
        }
    } catch (error) {
        if (!controller.signal.aborted) agentOwner?.invokeMethodAsync('AgentStreamError', 'The live stream ended. Task state remains available through refresh.').catch(() => {});
    }
}
