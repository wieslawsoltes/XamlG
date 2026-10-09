// Same-origin private storage, never available to the opaque-origin MCP/execution guests.
// Version comparison and replacement occur in a single IndexedDB transaction across tabs.
const limit = 16 * 1024 * 1024;
let database;
function validKey(key) {
  if (typeof key !== 'string' || !key.trim() || key.length > 256) throw new Error('invalid_workspace');
}
function validJson(json) {
  if (typeof json !== 'string' || new TextEncoder().encode(json).byteLength > limit) throw new Error('archive_limit');
}
function open() {
  return database ??= new Promise((resolve, reject) => {
    const request = indexedDB.open('xamlg.intelligent-ui', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('workspaces');
    request.onerror = () => { database = undefined; reject(request.error); };
    request.onblocked = () => { database = undefined; reject(new Error('storage_blocked: Close older Studio tabs before upgrading storage.')); };
    request.onsuccess = () => {
      const db = request.result;
      db.onversionchange = () => { db.close(); database = undefined; };
      resolve(db);
    };
  });
}
async function transact(key, operation, json, expectedVersion) {
  validKey(key); if (operation === 'write') validJson(json);
  const db = await open();
  const version = operation === 'write' ? crypto.randomUUID().replaceAll('-', '') : null;
  return new Promise((resolve, reject) => {
    const tx = db.transaction('workspaces', operation === 'read' ? 'readonly' : 'readwrite');
    const store = tx.objectStore('workspaces');
    let result, failure;
    tx.oncomplete = () => resolve(result);
    tx.onabort = tx.onerror = () => reject(failure ?? tx.error ?? new Error('storage_aborted'));
    const request = store.get(key);
    request.onsuccess = () => {
      try {
        const current = request.result ?? null;
        if (current) {
          validJson(current.json);
          if (typeof current.version !== 'string' || !/^[a-f0-9]{32}$/.test(current.version)) throw new Error('invalid_archive');
        }
        if (operation === 'read') { result = current; return; }
        if ((current?.version ?? null) !== (expectedVersion ?? null)) throw new Error('storage_conflict: Another tab changed this workspace. Reload before replacing it.');
        if (operation === 'write') { store.put({ version, json }, key); result = version; }
        else { store.delete(key); result = null; }
      } catch (error) { failure = error; tx.abort(); }
    };
  });
}
export const read = key => transact(key, 'read');
export const write = (key, json, version) => transact(key, 'write', json, version);
export const forget = (key, version) => transact(key, 'delete', null, version);
