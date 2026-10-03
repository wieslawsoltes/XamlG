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

export async function createEditor(host, dotnet, text, language, readOnly) {
  const id = ++sequence;
  try {
    const monaco = await loadMonaco();
    const model = monaco.editor.createModel(text, language);
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
      timer = setTimeout(() => dotnet.invokeMethodAsync('Changed', editor.getValue()), 120);
    });
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => dotnet.invokeMethodAsync('Run'));
    editors.set(id, { editor, model, subscription, set: value => { applying = true; try { editor.setValue(value); } finally { applying = false; } }, cleanup: () => clearTimeout(timer) });
  } catch (error) {
    const textarea = document.createElement('textarea');
    textarea.className = 'editor-fallback'; textarea.value = text; textarea.readOnly = readOnly;
    textarea.setAttribute('aria-label', language + ' source editor');
    textarea.oninput = () => dotnet.invokeMethodAsync('Changed', textarea.value);
    host.replaceChildren(textarea);
    editors.set(id, { textarea });
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
  // A programmatic document switch invalidates the old buffer's delayed notification.
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
  item?.cleanup?.(); item?.subscription?.dispose(); item?.editor?.dispose(); item?.model?.dispose();
  editors.delete(id);
}
export function setTheme(theme) {
  document.documentElement.dataset.theme = theme;
  localStorage.setItem('xamlg.theme', theme);
  self.monaco?.editor.setTheme(theme === 'light' ? 'vs' : 'vs-dark');
}
export function loadTheme() { return localStorage.getItem('xamlg.theme') ?? 'dark'; }
export function saveDraft(xaml, code) { localStorage.setItem('xamlg.draft', JSON.stringify({ version: 1, xaml, code })); }
export function loadDraft() {
  try { const value = JSON.parse(localStorage.getItem('xamlg.draft')); return value?.version === 1 ? value : null; }
  catch { return null; }
}
export function download(name, content, type = 'text/plain') {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
