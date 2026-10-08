const { installCSharpLanguage } = await (globalThis.xamlgBoot?.importModule('csharp-language.js') ?? import('./csharp-language.js'));
const { SourceBuffer } = await (globalThis.xamlgBoot?.importModule('source-buffer.js') ?? import('./source-buffer.js'));
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
    const source = new SourceBuffer(text, true);
    host.dataset.documentPath = path ?? '';
    const editor = monaco.editor.create(host, {
      model, readOnly, automaticLayout: true, theme: document.documentElement.dataset.theme === 'light' ? 'vs' : 'vs-dark',
      minimap: { enabled: false }, fontSize: 13, lineHeight: 21, padding: { top: 14 },
      scrollBeyondLastLine: false, roundedSelection: false, tabSize: 2, wordWrap: 'off',
      renderLineHighlight: 'line', smoothScrolling: true, bracketPairColorization: { enabled: true }
    });
    let timer;
    let applying = false;
    const subscription = editor.onDidChangeModelContent(event => {
      if (applying || readOnly) return;
      if (event.isFlush) source.set(model.getValue(undefined, true));
      else if (event.isEolChange) source.changeEol(event.eol);
      else source.applyChanges(event.changes);
      clearTimeout(timer);
      timer = setTimeout(() => {
        if (editors.has(id)) return dotnet.invokeMethodAsync('Changed', source.text);
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
    editors.set(id, { editor, model, source, subscription, path, dotnet, set: value => { applying = true; try { source.set(value); editor.setValue(value); } finally { applying = false; } }, cleanup: () => clearTimeout(timer) });
  } catch (error) {
    const textarea = document.createElement('textarea');
    textarea.className = 'editor-fallback'; textarea.value = text; textarea.readOnly = readOnly;
    const source = new SourceBuffer(text); let displayed = textarea.value;
    textarea.setAttribute('aria-label', language + ' source editor');
    textarea.oninput = () => { source.replaceDisplayed(displayed, textarea.value); displayed = textarea.value; return dotnet.invokeMethodAsync('Changed', source.text); };
    host.replaceChildren(textarea);
    host.dataset.documentPath = path ?? '';
    editors.set(id, { textarea, source, path, dotnet, set: value => { source.set(value); textarea.value = value; displayed = textarea.value; } });
    console.warn(error.message);
  }
  return id;
}

export function getEditorText(id) {
  const item = editors.get(id);
  if (!item) throw new Error('The source editor has been disposed.');
  item.cleanup?.();
  return item.source.text;
}
export function setEditorText(id, value) {
  const item = editors.get(id);
  if (!item) return;
  item.cleanup?.();
  if (item.source.text !== value) item.set(value);
}
export function reveal(id, start, length, focus = true) {
  const item = editors.get(id);
  if (item?.editor) {
    const from = item.source.positionAt(start), to = item.source.positionAt(start + length);
    item.editor.setSelection(new self.monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column));
    item.editor.revealPositionInCenter(from); if (focus) item.editor.focus();
  } else if (item?.textarea) {
    const displayed = new SourceBuffer(item.textarea.value);
    if (focus) item.textarea.focus();
    item.textarea.setSelectionRange(displayed.offsetAt(item.source.positionAt(start)), displayed.offsetAt(item.source.positionAt(start + length)));
  }
}
export function setMarkers(id, diagnostics) {
  const item = editors.get(id);
  if (!item?.model) return;
  self.monaco.editor.setModelMarkers(item.model, 'xamlg', diagnostics.map(d => ({
    code: d.code, message: d.message, severity: d.severity === 'Error' ? 8 : 4,
    startLineNumber: d.startLine, startColumn: item.source.displayColumn(d.startLine, d.startColumn), endLineNumber: d.endLine,
    endColumn: item.source.displayColumn(d.endLine, d.endLine === d.startLine ? Math.max(d.endColumn, d.startColumn + 1) : d.endColumn)
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
export function loadLiveUpdates() {
  try {
    const value = JSON.parse(localStorage.getItem('xamlg.live-updates'));
    return { compile: typeof value?.compile === 'boolean' ? value.compile : true,
      preview: typeof value?.preview === 'boolean' ? value.preview : true };
  } catch { return { compile: true, preview: true }; }
}
export function saveLiveUpdates(compile, preview) {
  try { localStorage.setItem('xamlg.live-updates', JSON.stringify({ compile, preview })); }
  catch { /* The live session still works when browser storage is unavailable. */ }
}
export function resetAgentPanelScroll(content, section) {
  if (!content?.isConnected) return;
  content.scrollTop = 0;
  const nav = content.closest('.agent-workbench')?.querySelector('.agent-navigation');
  const button = [...(nav?.querySelectorAll('button') || [])].find(item => item.getAttribute('aria-label') === section);
  if (!button) return;
  const outer = nav.getBoundingClientRect(), inner = button.getBoundingClientRect();
  if (inner.left < outer.left) nav.scrollLeft -= outer.left - inner.left;
  else if (inner.right > outer.right) nav.scrollLeft += inner.right - outer.right;
}
export function saveDraft(xaml, code, resources = {}, codeFiles = {}, compilerOptions = null) {
  localStorage.setItem('xamlg.draft', JSON.stringify({ version: 4, xaml, code, resources, codeFiles, compilerOptions }));
}
export function loadDraft() {
  try {
    const source = localStorage.getItem('xamlg.draft');
    if (!source || source.length > 12 * 1024 * 1024) return null;
    const value = JSON.parse(source);
    return [1, 2, 3, 4].includes(value?.version) && typeof value.xaml === 'string' && typeof value.code === 'string' ? value : null;
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
    start = item.source.offsetAt(selection.getStartPosition());
    length = item.source.offsetAt(selection.getEndPosition()) - start;
  } else {
    const displayed = new SourceBuffer(item.textarea.value);
    start = item.source.offsetAt(displayed.positionAt(item.textarea.selectionStart));
    length = item.source.offsetAt(displayed.positionAt(item.textarea.selectionEnd)) - start;
  }
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
    try { localStorage.setItem('xamlg.dockyard.layout.v3', layout); } catch { }
}
export function loadDockyardLayout() {
    try { return localStorage.getItem('xamlg.dockyard.layout.v3'); } catch { return null; }
}

// Layouts contain geometry and file identities only. A prior project's documents
// must never be recreated from its layout, and all content uses registered templates.
export function filterDockyardLayout(text, ids) {
    if (typeof text !== 'string' || text.length > 4 * 1024 * 1024) throw new Error('Invalid workspace layout.');
    const data = JSON.parse(text), allowed = new Set(ids);
    const visit = node => {
        if (!node || typeof node !== 'object') return node;
        // Dockyard 0.2.2's browser bundle names these two constructors with an
        // esbuild prefix, but its layout reader accepts the public type names.
        if (node.type === '_LayoutAnchorablePaneGroup' || node.type === '_LayoutDocumentPaneGroup') node.type = node.type.slice(1);
        if (node.type === 'LayoutDocument' || node.type === 'LayoutAnchorable') {
            if (!allowed.has(node.props?.ContentId)) return null;
            if (node.props.ContentId === 'preview') { node.props.CanClose = false; node.props.CanHide = false; }
        }
        if (node.children) node.children = node.children.map(visit).filter(Boolean);
        if (node.rootPanel) node.rootPanel = visit(node.rootPanel);
        if (node.sides) for (const key of Object.keys(node.sides)) node.sides[key] = visit(node.sides[key]);
        if (node.floatingWindows) node.floatingWindows = node.floatingWindows.map(visit).filter(node => node && node.children?.length);
        if (node.hidden) node.hidden = node.hidden.map(visit).filter(Boolean);
        return node;
    };
    visit(data.layout);
    return JSON.stringify(data);
}
export function dockyardContents(manager) {
    return [...manager.Layout.Descendents()].filter(item => item.ContentId).map(item =>
        ({ id: item.ContentId, title: item.Title, active: item.IsActive, hidden: !!item.IsHidden }));
}
export function activateDockContent(manager, id, focus = true) {
    const item = manager.Find(id);
    if (!item) return false;
    if (focus) return manager.Activate(item);
    // Designer highlighting must not steal keyboard focus from an active gesture.
    item.IsSelected = true;
    return true;
}
export function reconcileDockDocuments(manager, ids, registered = []) {
    const current = new Set(ids);
    const obsolete = dockyardContents(manager).filter(item => /^(document|generated):/.test(item.id) && !current.has(item.id));
    manager.Transaction('Retire removed documents', () => {
        for (const item of obsolete) {
            const model = manager.Find(item.id);
            model?.Parent?.RemoveChild(model);
        }
    });
    for (const id of new Set([...obsolete.map(item => item.id), ...registered.filter(id => !current.has(id))])) manager.ReleaseContent(id);
}
export function installDockyardWorkspace(manager, owner) {
    let disposed = false;
    const pending = new Set(), permitted = new Set();
    const protect = operation => (_sender, args) => {
        const item = args.Model, id = item?.ContentId;
        if (!id || permitted.has(id) || !(/^(document|generated):/.test(id) || id === 'agent' || id === 'agent-access')) return;
        args.Cancel = true;
        if (pending.has(id) || disposed) return;
        pending.add(id);
        owner.invokeMethodAsync('PrepareDockContentClose', id).then(allowed => {
            if (disposed || !allowed || manager.Find(id) !== item) return;
            permitted.add(id);
            try { manager[operation](item); } finally { permitted.delete(id); }
        }).catch(error => console.error('Could not close workspace content', error)).finally(() => pending.delete(id));
    };
    const subscriptions = [manager.DocumentClosing.add(protect('Close')), manager.AnchorableClosing.add(protect('Close')),
        manager.AnchorableHiding.add(protect('Hide')),
        manager.ActiveContentChanged.add((_sender, args) => {
            if (!disposed && args.Model?.ContentId) owner.invokeMethodAsync('DockContentActivated', args.Model.ContentId).catch(() => {});
        })];
    return { dispose() { disposed = true; for (const unsubscribe of subscriptions) unsubscribe(); } };
}

let automationOwner = null;
let automationSocket = null;
let agentConnection = null;
let agentOwner = null;
let agentOwnerId = null;
let agentStream = null;
let resourceNotificationTimer = null;
const changedAutomationResources = new Set();
export function notifyAutomationResource(uri) {
    const socket = automationSocket;
    if (!socket || socket.readyState !== WebSocket.OPEN) return;
    if (changedAutomationResources.size < 1024) changedAutomationResources.add(uri);
    if (resourceNotificationTimer) return;
    resourceNotificationTimer = setTimeout(() => {
        resourceNotificationTimer = null;
        const uris = [...changedAutomationResources]; changedAutomationResources.clear();
        if (automationSocket !== socket || socket.readyState !== WebSocket.OPEN) return;
        for (let offset = 0; offset < uris.length; offset += 128)
            socket.send(JSON.stringify({ kind: 'resources_changed', uris: uris.slice(offset, offset + 128) }));
    }, 100);
}
export function installAutomation(owner) {
    automationOwner = owner;
    window.xamlgAutomation = Object.freeze({
        catalog: () => owner.invokeMethodAsync('AutomationCatalog'),
        call: (name, args = {}) => owner.invokeMethodAsync('AutomationInvoke', crypto.randomUUID(), 'call', name, args, 'Browser automation', 'studio-owner'),
        resource: uri => owner.invokeMethodAsync('AutomationInvoke', crypto.randomUUID(), 'resource', uri, {}, 'Browser automation', 'studio-owner')
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
                const result = await automationOwner.invokeMethodAsync('AutomationInvoke', id, request.method, request.name, request.arguments ?? {}, request.caller ?? 'MCP', request.principalId ?? 'mcp');
                if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: request.id, result }));
            } catch (error) {
                if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: request.id, error: { code: 'ide_error', message: String(error.message || error) } }));
            } finally { pending.delete(id); }
        };
    });
}
export function disconnectAutomation() {
    clearTimeout(resourceNotificationTimer); resourceNotificationTimer = null; changedAutomationResources.clear();
    agentConnection = null; agentStream?.abort(); agentStream = null;
    if (automationSocket) { automationSocket.close(); automationSocket = null; }
    agentOwner?.invokeMethodAsync('AgentDisconnected').catch(() => {});
}

export function installAgentWorkbench(owner, id) { if (agentOwnerId && agentOwnerId !== id) releaseAgentViews(agentOwnerId); agentOwner = owner; agentOwnerId = id; }
export function uninstallAgentWorkbench(id) { releaseAgentViews(id); if (agentOwnerId === id) { agentOwner = null; agentOwnerId = null; } }
export function agentConnected() { return !!agentConnection; }
export async function copyAgentText(text) {
    try { await navigator.clipboard.writeText(text); return; } catch { }
    const active = document.activeElement, selection = window.getSelection();
    const ranges = selection ? Array.from({ length: selection.rangeCount }, (_, i) => selection.getRangeAt(i).cloneRange()) : [];
    const buffer = document.createElement('textarea'); buffer.value = text; buffer.readOnly = true;
    buffer.setAttribute('aria-label', 'Copy text'); buffer.style.cssText = 'position:fixed;left:-10000px;top:0';
    document.body.append(buffer); buffer.select();
    try { if (!document.execCommand('copy')) throw new Error('Copy is unavailable. Select the message and use your browser’s Copy command.'); }
    finally {
        buffer.remove(); active?.focus({ preventScroll: true });
        if (selection) { selection.removeAllRanges(); for (const range of ranges) selection.addRange(range); }
    }
}
export function downloadBytes(name, bytes, mimeType) {
    const url = URL.createObjectURL(new Blob([bytes], { type: mimeType }));
    const link = document.createElement('a'); link.href = url; link.download = name; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
const agentThreadPositions = new Map();
const agentThreadBindings = new WeakMap();
const agentViewNodes = new Map();
function rememberAgentView(ownerId, kind, element) {
    if (!ownerId) return;
    if (!element) { releaseAgentViewKind(ownerId, kind); return; }
    const nodes = agentViewNodes.get(ownerId) || {};
    if (nodes[kind] && nodes[kind] !== element) releaseAgentViewKind(ownerId, kind);
    nodes[kind] = element; agentViewNodes.set(ownerId, nodes);
}
export function releaseAgentViewKind(ownerId, kind) {
    const nodes = agentViewNodes.get(ownerId), element = nodes?.[kind];
    if (!element) return;
    if (kind === 'thread') releaseAgentThread(element);
    else if (kind === 'composer') releaseAgentComposer(element);
    else if (kind === 'diff') releaseAgentDiff(element);
    delete nodes[kind];
    if (!Object.keys(nodes).length) agentViewNodes.delete(ownerId);
}
export function releaseAgentViews(ownerId) { for (const kind of ['thread', 'composer', 'diff']) releaseAgentViewKind(ownerId, kind); }
const agentComposerBindings = new WeakMap();
export function bindAgentComposer(element, taskId, owner, ownerId) {
    rememberAgentView(ownerId, 'composer', element);
    if (!element || agentComposerBindings.get(element)?.taskId === taskId) return;
    releaseAgentComposer(element);
    let composing = false;
    const start = () => { composing = true; };
    const end = () => { composing = false; };
    const key = event => {
        if (event.key !== 'Enter' || event.shiftKey || event.isComposing || event.keyCode === 229 || composing) return;
        if (element.dataset.taskId !== taskId || element.dataset.canSubmit !== 'true') return;
        event.preventDefault(); event.stopPropagation();
        if (event.repeat) return;
        // The managed review state rejects duplicate submissions. Draft persistence
        // may still be pending after the user cancels a review and submits again.
        owner.invokeMethodAsync('AgentComposerSubmit', taskId, element.value)
            .catch(() => {});
    };
    element.addEventListener('compositionstart', start); element.addEventListener('compositionend', end); element.addEventListener('keydown', key);
    agentComposerBindings.set(element, { taskId, start, end, key });
}
export function releaseAgentComposer(element) {
    const binding = element && agentComposerBindings.get(element);
    if (!binding) return;
    element.removeEventListener('compositionstart', binding.start); element.removeEventListener('compositionend', binding.end); element.removeEventListener('keydown', binding.key);
    agentComposerBindings.delete(element);
}
const agentDiffPositions = new Map();
const agentDiffBindings = new WeakMap();
export function bindAgentDiff(element, key, ownerId) {
    rememberAgentView(ownerId, 'diff', element);
    if (!element || agentDiffBindings.get(element)?.key === key) return;
    releaseAgentDiff(element);
    const remember = () => {
        if (element.clientHeight && element.dataset.reviewKey === key) agentDiffPositions.set(key, element.scrollTop);
        while (agentDiffPositions.size > 64) agentDiffPositions.delete(agentDiffPositions.keys().next().value);
    };
    const restore = () => { if (element.clientHeight && element.dataset.reviewKey === key) element.scrollTop = agentDiffPositions.get(key) || 0; };
    const resize = new ResizeObserver(restore); resize.observe(element);
    element.addEventListener('scroll', remember, { passive: true });
    agentDiffBindings.set(element, { key, remember, resize }); restore();
}
export function releaseAgentDiff(element) {
    const binding = element && agentDiffBindings.get(element);
    if (binding) { binding.resize.disconnect(); element.removeEventListener('scroll', binding.remember); agentDiffBindings.delete(element); }
}
export function revealAgentBlock(element, id) {
    [...element?.querySelectorAll('[data-block-id]') || []].find(line => line.dataset.blockId === id)?.scrollIntoView({ block: 'nearest' });
}
export function bindAgentThread(element, taskId, ownerId) {
    rememberAgentView(ownerId, 'thread', element);
    if (!element || agentThreadBindings.get(element)?.taskId === taskId) return;
    releaseAgentThread(element);
    let position = agentThreadPositions.get(taskId);
    if (!position) {
        position = { follow: true, top: 0, anchor: null, offset: 0, expanded: new Set() };
        agentThreadPositions.set(taskId, position);
        while (agentThreadPositions.size > 16) agentThreadPositions.delete(agentThreadPositions.keys().next().value);
    }
    function remember() {
        if (element.dataset.taskId !== taskId || !element.clientHeight) return;
        const following = position.follow;
        position.top = element.scrollTop;
        position.follow = element.scrollHeight - element.scrollTop - element.clientHeight < 32;
        const top = element.getBoundingClientRect().top;
        const first = [...element.querySelectorAll('[data-sequence]')].find(item => item.getBoundingClientRect().bottom >= top);
        position.anchor = first?.dataset.sequence;
        position.offset = first ? first.getBoundingClientRect().top - top : 0;
        if (position.follow !== following && agentOwnerId === ownerId)
            agentOwner?.invokeMethodAsync('AgentThreadFollowing', taskId, position.follow).catch(() => {});
    }
    function restore() {
        if (element.dataset.taskId !== taskId || !element.clientHeight) return;
        for (const detail of element.querySelectorAll('details[data-sequence]')) {
            if (detail.dataset.expansionRestored === taskId) continue;
            detail.open = position.expanded.has(detail.dataset.sequence); detail.dataset.expansionRestored = taskId;
        }
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
    const resize = new ResizeObserver(restore); resize.observe(element);
    const toggle = event => {
        if (!event.target.matches('details[data-sequence]') || element.dataset.taskId !== taskId) return;
        const sequence = event.target.dataset.sequence;
        if (event.target.open) position.expanded.add(sequence); else position.expanded.delete(sequence);
        while (position.expanded.size > 1200) position.expanded.delete(position.expanded.values().next().value);
    };
    element.addEventListener('toggle', toggle, true);
    element.addEventListener('scroll', remember, { passive: true });
    agentThreadBindings.set(element, { taskId, observer, resize, remember, toggle, position }); restore();
}
export function releaseAgentThread(element) {
    const binding = agentThreadBindings.get(element);
    if (binding) { binding.observer.disconnect(); binding.resize.disconnect(); element.removeEventListener('scroll', binding.remember); element.removeEventListener('toggle', binding.toggle, true); agentThreadBindings.delete(element); }
}
export function followAgentThread(element) {
    const binding = agentThreadBindings.get(element);
    if (binding) binding.position.follow = true;
    if (element) element.scrollTop = element.scrollHeight;
}
export function holdAgentThread(element) {
    const binding = element && agentThreadBindings.get(element);
    if (binding) binding.position.follow = false;
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
export async function beginChatGptSignIn(argumentsValue) {
  const popup = window.open('about:blank', '_blank');
  if (popup) popup.opener = null;
  try {
    const result = await agentRequest('chatgpt_sign_in', argumentsValue);
    const launch = new URL(result.launchUrl);
    if (launch.protocol !== 'http:' || launch.hostname !== '127.0.0.1' || !launch.port || launch.pathname !== '/auth/start' || launch.username || launch.password)
      throw new Error('Invalid companion sign-in link.');
    if (popup && !popup.closed) popup.location.replace(launch.href);
    return result;
  } catch (error) { if (popup && !popup.closed) popup.close(); throw error; }
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

export function installStudioShell() {
    const menus = () => [...document.querySelectorAll('.studio-menubar > details')];
    const close = except => { for (const menu of menus()) if (menu !== except) menu.open = false; };
    const click = event => {
        const target = event.target instanceof Element ? event.target : null;
        const menu = target?.closest('.studio-menu');
        if (target?.closest('summary') && menu) close(menu);
        else if (!menu || target?.closest('button')) close();
    };
    const keydown = event => {
        const target = event.target instanceof Element ? event.target : null;
        const menu = target?.closest('.studio-menu');
        if (!menu) return;
        // Native selects own their arrow keys, including the example picker.
        if (target?.tagName === 'SELECT' && event.key !== 'Escape') return;
        if (event.key === 'Escape') { menu.open = false; menu.querySelector('summary')?.focus(); event.preventDefault(); }
        else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            menu.open = true;
            const items = [...menu.querySelectorAll('button:not(:disabled), select:not(:disabled)')];
            const index = items.indexOf(target), direction = event.key === 'ArrowDown' ? 1 : -1;
            const next = index < 0 ? (direction > 0 ? 0 : items.length - 1) : (index + direction + items.length) % items.length;
            items[next]?.focus(); event.preventDefault();
        } else if ((event.key === 'ArrowLeft' || event.key === 'ArrowRight') && target?.tagName !== 'SELECT') {
            const all = menus(), index = all.indexOf(menu), next = all[(index + (event.key === 'ArrowRight' ? 1 : -1) + all.length) % all.length];
            const opened = menu.open; close(); next.open = opened; next.querySelector('summary')?.focus(); event.preventDefault();
        }
    };
    document.addEventListener('click', click);
    document.addEventListener('keydown', keydown);
    return { dispose() { document.removeEventListener('click', click); document.removeEventListener('keydown', keydown); } };
}
