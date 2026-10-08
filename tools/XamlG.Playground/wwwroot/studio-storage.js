// Private, versioned browser state. Credentials and native provider continuations never
// enter localStorage, public transcripts, project downloads or diagnostics.
const databaseName = 'xamlg.studio.state.v1';
const revisions = new Map(), lastValues = new Map();
let database, queue = Promise.resolve(), lastError = null;
const encoder = new TextEncoder(), decoder = new TextDecoder();
function request(value) { return new Promise((resolve, reject) => { value.onsuccess = () => resolve(value.result); value.onerror = () => reject(value.error); }); }
function done(tx) { return new Promise((resolve, reject) => { tx.oncomplete = resolve; tx.onabort = tx.onerror = () => reject(tx.error || new Error('State transaction failed.')); }); }
async function open() {
    if (!database) database = new Promise((resolve, reject) => {
        const pending = indexedDB.open(databaseName, 1);
        pending.onupgradeneeded = () => pending.result.createObjectStore('records');
        pending.onsuccess = () => { const db = pending.result; db.onversionchange = () => { db.close(); database = null; }; resolve(db); };
        pending.onerror = () => { database = null; reject(pending.error); };
        pending.onblocked = () => reject(new Error('Close other Studio tabs to upgrade saved state.'));
    });
    return database;
}
async function read(key) { const db = await open(); return request(db.transaction('records').objectStore('records').get(key)); }
async function encryptionKey() {
    const existing = await read('$key');
    if (existing) return existing;
    const generated = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    const db = await open(), tx = db.transaction('records', 'readwrite'), complete = done(tx), records = tx.objectStore('records');
    const current = await request(records.get('$key'));
    if (!current) records.put(generated, '$key');
    await complete; return current || generated;
}
function failure(error) {
    lastError = error?.name === 'QuotaExceededError' ? 'Browser storage is full. Free space or export your project before closing Studio.' :
        error?.message === 'state_conflict' ? 'Another Studio tab changed saved state. Reload this tab before saving further changes.' :
        'Studio could not save or restore local state. Keep this tab open and export your project.';
    globalThis.dispatchEvent?.(new CustomEvent('xamlg-storage-status', { detail: { error: lastError } }));
    return new Error(lastError);
}
function serial(action) {
    const operation = queue.catch(() => {}).then(action);
    queue = operation;
    return operation.catch(error => { throw failure(error); });
}
async function decode(record, key) {
    if (!record?.data) return null;
    if (record.version !== 1) throw new Error('Unsupported saved state version.');
    const bytes = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: record.iv, additionalData: encoder.encode(key) }, await encryptionKey(), record.data);
    return JSON.parse(decoder.decode(bytes));
}
export function loadStudioState(key) {
    return serial(async () => {
        const record = await read(key);
        revisions.set(key, record?.revision || 0);
        try { const value = await decode(record, key); lastValues.set(key, JSON.stringify(value)); return value; }
        catch (error) {
            const previous = await read(key + ':previous');
            if (!previous) throw error;
            const value = await decode(previous, key);
            failure(error); // Recover the last complete write, and make the recovery visible.
            return value;
        }
    });
}
export function saveStudioState(key, value) {
    return serial(async () => {
        const text = JSON.stringify(value);
        if (lastValues.get(key) === text) return;
        const bytes = encoder.encode(text);
        if (bytes.length > 96 * 1024 * 1024) throw new Error('Saved state is too large.');
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const data = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: encoder.encode(key) }, await encryptionKey(), bytes);
        const db = await open(), tx = db.transaction('records', 'readwrite'), complete = done(tx), records = tx.objectStore('records');
        const previous = await request(records.get(key)), revision = previous?.revision || 0;
        if (revisions.has(key) && revisions.get(key) !== revision) { tx.abort(); await complete.catch(() => {}); throw new Error('state_conflict'); }
        // A first write without a load must not overwrite another tab's state.
        if (!revisions.has(key) && previous?.data) { tx.abort(); await complete.catch(() => {}); throw new Error('state_conflict'); }
        if (previous?.data) records.put(previous, key + ':previous');
        records.put({ version: 1, revision: revision + 1, iv, data }, key);
        await complete;
        revisions.set(key, revision + 1); lastValues.set(key, text); lastError = null;
        globalThis.dispatchEvent?.(new CustomEvent('xamlg-storage-status', { detail: { error: null } }));
    });
}
export function forgetStudioState(key) {
    return serial(async () => {
        const db = await open(), tx = db.transaction('records', 'readwrite'), complete = done(tx), records = tx.objectStore('records');
        const previous = await request(records.get(key)), revision = (previous?.revision || 0) + 1;
        records.delete(key + ':previous'); records.put({ version: 1, revision }, key);
        await complete; revisions.set(key, revision); lastValues.delete(key);
    });
}
export async function studioStorageStatus() {
    const estimate = await navigator.storage?.estimate?.();
    return { error: lastError, usage: estimate?.usage ?? null, quota: estimate?.quota ?? null };
}
