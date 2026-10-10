const databaseName = 'xamlg.solution-workspace.v1';
const maximumFiles = 20000, maximumFileBytes = 3 * 1024 * 1024, maximumCharacters = 32 * 1024 * 1024;
let databasePromise, unsavedChanges = false;

function database() {
  if (databasePromise) return databasePromise;
  let abandoned = false;
  const pending = new Promise((resolve, reject) => {
    if (!globalThis.indexedDB) { reject(new Error('IndexedDB is unavailable. Browser workspaces require persistent browser storage.')); return; }
    const request = indexedDB.open(databaseName, 1);
    request.onupgradeneeded = () => { if (!request.result.objectStoreNames.contains('state')) request.result.createObjectStore('state'); };
    request.onerror = () => { abandoned = true; reject(request.error ?? new Error('Workspace storage could not be opened.')); };
    request.onblocked = () => { abandoned = true; reject(new Error('Another tab is blocking the workspace storage upgrade.')); };
    request.onsuccess = () => {
      const db = request.result;
      // A blocked open may complete after the caller has already retried. It must
      // not retain an unreachable connection that blocks future schema upgrades.
      if (abandoned) { db.close(); return; }
      db.onversionchange = () => { db.close(); if (databasePromise === pending) databasePromise = null; };
      resolve(db);
    };
  });
  databasePromise = pending;
  // Also retire synchronous failures (unavailable storage, SecurityError). The
  // same module can recover without reloading or bypassing revision checks.
  pending.catch(() => { if (databasePromise === pending) databasePromise = null; });
  return pending;
}

export async function loadWorkspace() {
  const db = await database();
  return new Promise((resolve, reject) => {
    const transaction = db.transaction('state', 'readonly');
    const request = transaction.objectStore('state').get('current');
    request.onsuccess = () => resolve(request.result ?? null);
    request.onerror = () => reject(request.error ?? new Error('Could not read the saved solution.'));
    transaction.onabort = () => reject(transaction.error ?? new Error('Workspace read was aborted.'));
  });
}

export async function saveWorkspace(state, expectedRevision) {
  if (!state || state.format !== 1 || !Array.isArray(state.files) || state.files.length > maximumFiles ||
      !Number.isSafeInteger(expectedRevision) || expectedRevision < 0 || expectedRevision === Number.MAX_SAFE_INTEGER) throw new Error('Invalid workspace snapshot.');
  let total = 0;
  for (const file of state.files) {
    if (!file || typeof file.path !== 'string' || typeof file.content !== 'string' || file.content.length > 4 * 1024 * 1024 ||
        (total += file.content.length) > maximumCharacters) throw new Error('Workspace storage content limit exceeded.');
  }
  const db = await database();
  return new Promise((resolve, reject) => {
    const transaction = db.transaction('state', 'readwrite');
    const store = transaction.objectStore('state');
    let failure, revision;
    const current = store.get('current');
    current.onerror = () => { failure = current.error; };
    current.onsuccess = () => {
      const actual = current.result?.revision ?? 0;
      if (!Number.isSafeInteger(actual) || actual < 0 || actual !== expectedRevision) {
        failure = new Error('Another tab changed the saved workspace. Export your current buffers before reloading; the newer saved workspace was not overwritten.');
        transaction.abort(); return;
      }
      revision = actual + 1;
      store.put({ ...state, revision }, 'current');
    };
    transaction.oncomplete = () => resolve(revision);
    transaction.onabort = transaction.onerror = () => reject(failure ?? transaction.error ?? new Error('The browser could not save the workspace. Check available storage space.'));
  });
}

export async function nativeRequest(action, args) {
  if (typeof action !== 'string' || !action.startsWith('workspace_')) throw new Error('Invalid workspace action.');
  const studio = await (globalThis.xamlgBoot?.importModule('studio.js') ?? import('./studio.js'));
  return studio.agentRequest(action, args);
}

function pickFiles(kind) {
  return new Promise((resolve, reject) => {
    const input = document.createElement('input'); input.type = 'file'; input.hidden = true;
    if (kind === 'folder') { input.webkitdirectory = true; input.multiple = true; }
    else if (kind === 'zip') { input.accept = '.zip,application/zip'; }
    else input.multiple = true;
    let finished = false, timer;
    const finish = files => {
      if (finished) return; finished = true;
      clearTimeout(timer); window.removeEventListener('focus', focused); input.remove(); resolve(files);
    };
    const focused = () => { timer = setTimeout(() => { if (!input.files?.length) finish(null); }, 500); };
    input.addEventListener('change', () => finish(Array.from(input.files ?? [])), { once: true });
    input.addEventListener('cancel', () => finish(null), { once: true });
    window.addEventListener('focus', focused);
    document.body.append(input);
    try { input.click(); } catch (error) { input.remove(); window.removeEventListener('focus', focused); reject(error); }
  });
}

function base64(bytes) {
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 8192) binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
  return btoa(binary);
}
function decode(bytes) {
  try {
    let encoding = 'utf-8';
    if (bytes[0] === 0xff && bytes[1] === 0xfe) encoding = 'utf-16le';
    else if (bytes[0] === 0xfe && bytes[1] === 0xff) encoding = 'utf-16be';
    const text = new TextDecoder(encoding, { fatal: true }).decode(bytes);
    if (!text.includes('\0')) return { content: text, isBinary: false };
  } catch { }
  return { content: base64(bytes), isBinary: true };
}

export async function chooseImport(kind) {
  if (!['folder', 'files', 'zip'].includes(kind)) throw new Error('Unknown import mode.');
  const selected = await pickFiles(kind);
  if (!selected?.length) return null;
  if (kind === 'zip') {
    if (selected[0].size > 64 * 1024 * 1024) throw new Error('ZIP archive limit is 64 MiB.');
    return DotNet.invokeMethodAsync('XamlG.Playground', 'ImportSolutionArchive', new Uint8Array(await selected[0].arrayBuffer()));
  }
  if (selected.length > maximumFiles) throw new Error('Workspace file count limit exceeded. Choose a smaller source folder.');
  const ignored = new Set(['.git', '.hg', '.svn', 'bin', 'obj', 'node_modules']);
  const imported = []; let bytesRead = 0, characters = 0;
  for (const file of selected) {
    let path = kind === 'folder' ? file.webkitRelativePath : file.name;
    if (kind === 'folder') path = path.slice(path.indexOf('/') + 1);
    if (path.split('/').some(part => ignored.has(part.toLowerCase()) || part.startsWith('.xamlg-'))) continue;
    if (file.size > maximumFileBytes || (bytesRead += file.size) > maximumCharacters)
      throw new Error('Source import exceeds the 3 MiB per-file or 32 MiB total byte limit. No files were imported.');
    const decoded = decode(new Uint8Array(await file.arrayBuffer()));
    if ((characters += decoded.content.length) > maximumCharacters) throw new Error('Decoded workspace content limit exceeded.');
    imported.push({ path, ...decoded });
  }
  return imported;
}

export async function exportWorkspace(name, files) {
  const bytes = await DotNet.invokeMethodAsync('XamlG.Playground', 'ExportSolutionArchive', files);
  downloadArchive(name, bytes);
}
export function downloadArchive(name, bytes) {
  const blob = new Blob([bytes], { type: 'application/zip' });
  const url = URL.createObjectURL(blob), link = document.createElement('a');
  link.href = url; link.download = `${name || 'Workspace'}.zip`; document.body.append(link); link.click(); link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export function confirmDiscard(message) { return globalThis.confirm(message); }
export function confirmDocumentClose(path) {
  if (globalThis.confirm(`Save changes to ${path}?\nChoose OK to save, or Cancel for discard/keep-open choices.`)) return 'save';
  return globalThis.confirm(`Discard unsaved changes to ${path}?\nChoose Cancel to keep the document open.`) ? 'discard' : 'cancel';
}
export function setUnsavedChanges(value) { unsavedChanges = !!value; }
globalThis.addEventListener('beforeunload', event => {
  if (!unsavedChanges) return;
  event.preventDefault(); event.returnValue = '';
});
export async function editorHistory(documentKey, operation) {
  if (!['undo', 'redo'].includes(operation)) throw new Error('Unsupported history operation.');
  const model = globalThis.monaco?.editor.getModels().find(model => model.uri.path.endsWith('/' + documentKey));
  if (model) await model[operation]();
}
export function installShortcuts(dotnet) {
  const handler = event => {
    if (!(event.ctrlKey || event.metaKey) || event.altKey || event.key.toLowerCase() !== 's') return;
    if (!event.target?.closest?.('[data-workspace-document]')) return;
    event.preventDefault(); event.stopPropagation();
    dotnet.invokeMethodAsync('SaveWorkspaceDocuments').catch(error => console.error('Workspace save failed', error));
  };
  document.addEventListener('keydown', handler, true);
  return { dispose() { document.removeEventListener('keydown', handler, true); } };
}
