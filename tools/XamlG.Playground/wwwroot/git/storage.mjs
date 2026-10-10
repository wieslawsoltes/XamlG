import { GitError, SerialQueue } from './core.mjs';

/** Revisions and blobs are committed in one IndexedDB transaction; credentials are never accepted here. */
export class WorkspaceStore {
    #database;
    constructor(factory = globalThis.indexedDB) {
        this.#database = new Promise((resolve, reject) => {
            if (!factory) { reject(new GitError('storage_unavailable', 'Persistent repository storage requires IndexedDB.')); return; }
            const request = factory.open('xamlg-git-workspaces-v1', 1);
            request.onupgradeneeded = () => { request.result.createObjectStore('workspaces', { keyPath: 'id' }); request.result.createObjectStore('blobs'); };
            request.onsuccess = () => { request.result.onversionchange = () => request.result.close(); resolve(request.result); };
            request.onerror = () => reject(request.error);
            request.onblocked = () => reject(new GitError('storage_blocked', 'Close older Studio tabs before upgrading repository storage.'));
        });
    }
    async #read(store, key) { const db = await this.#database; return new Promise((resolve, reject) => { const request = db.transaction(store).objectStore(store)[key === undefined ? 'getAll' : 'get'](key); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); }); }
    load(id) { return this.#read('workspaces', id); }
    list() { return this.#read('workspaces'); }
    blob(oid) { return this.#read('blobs', oid); }
    async save(state, expectedRevision, blobs = []) {
        const db = await this.#database;
        return new Promise((resolve, reject) => {
            const transaction = db.transaction(['workspaces', 'blobs'], 'readwrite');
            let error; const workspaces = transaction.objectStore('workspaces'), request = workspaces.get(state.id);
            request.onsuccess = () => {
                if ((request.result?.revision ?? -1) !== expectedRevision) { error = new GitError('stale_workspace', 'This repository changed in another tab. Refresh before retrying.'); transaction.abort(); return; }
                workspaces.put({ ...state, revision: expectedRevision + 1 });
                for (const [oid, bytes] of blobs) transaction.objectStore('blobs').put(bytes, oid);
            };
            transaction.oncomplete = () => resolve(expectedRevision + 1);
            transaction.onabort = transaction.onerror = () => reject(error ?? transaction.error ?? new GitError('storage_error', 'Repository storage could not be committed.'));
        });
    }
    async cacheBlob(oid, bytes) { const db = await this.#database; await new Promise((resolve, reject) => { const tx = db.transaction('blobs', 'readwrite'); tx.objectStore('blobs').put(bytes, oid); tx.oncomplete = resolve; tx.onabort = tx.onerror = () => reject(tx.error); }); }
    async remove(id, expectedRevision) {
        const db = await this.#database;
        await new Promise((resolve, reject) => { const tx = db.transaction('workspaces', 'readwrite'), store = tx.objectStore('workspaces'), r = store.get(id); let stale = false;
            r.onsuccess = () => { if (r.result?.revision !== expectedRevision) { stale = true; tx.abort(); } else store.delete(id); };
            tx.oncomplete = resolve; tx.onabort = tx.onerror = () => reject(stale ? new GitError('stale_workspace', 'Repository changed; refresh first.') : tx.error); });
    }
    async close() { (await this.#database).close(); }
}
export class MemoryWorkspaceStore {
    #states = new Map(); #blobs = new Map();
    async load(id) { return structuredClone(this.#states.get(id)); }
    async list() { return structuredClone([...this.#states.values()]); }
    async blob(oid) { return this.#blobs.get(oid)?.slice(); }
    async cacheBlob(oid, bytes) { this.#blobs.set(oid, bytes.slice()); }
    async save(state, revision, blobs = []) { if ((this.#states.get(state.id)?.revision ?? -1) !== revision) throw new GitError('stale_workspace', 'Repository changed.'); this.#states.set(state.id, structuredClone({ ...state, revision: revision + 1 })); for (const [oid, bytes] of blobs) this.#blobs.set(oid, bytes.slice()); return revision + 1; }
    async remove(id, revision) { if (this.#states.get(id)?.revision !== revision) throw new GitError('stale_workspace', 'Repository changed.'); this.#states.delete(id); }
}
const queues = new Map();
export function withWorkspaceLock(id, action) {
    if (globalThis.navigator?.locks) return navigator.locks.request(`xamlg-git:${id}`, { mode: 'exclusive' }, action);
    if (!queues.has(id)) queues.set(id, new SerialQueue());
    // The store's transactional revision check remains authoritative without Web Locks.
    return queues.get(id).run(action);
}
