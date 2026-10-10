import { GitError, base64, unbase64, utf8, requireValue } from './core.mjs';
/** Browser adapter for the owner-authenticated native companion. */
export class NativeRepository extends EventTarget {
    #client; #info; #state; #status; #history = []; stashes = '';
    native = true;
    constructor(client, info) { super(); this.#client = client; this.#info = info; }
    get state() { return structuredClone(this.#state); }
    status() { return structuredClone(this.#status); }
    history() { return structuredClone(this.#history); }
    raw(operation, args = {}, signal) { return this.#client.call('git/run', { id: this.#info.id, operation, args }, signal); }
    async refresh() {
        const { status, files, refs, history, stashes } = await this.raw('snapshot');
        this.#history = history; this.stashes = stashes.text;
        const change = entry => ({ path: entry.path, original: entry.original, kind: entry.code.includes('D') ? 'deleted' : entry.code.includes('A') || entry.code === '??' ? 'added' : 'modified' });
        this.#status = { branch: status.branch, head: status.head, staged: status.entries.filter(e => e.code[0] !== ' ' && e.code[0] !== '?').map(change), unstaged: status.entries.filter(e => e.code[1] !== ' ' || e.code === '??').map(change), conflicts: status.entries.filter(e => e.conflict).map(e => e.path), operation: status.operation };
        this.#state = { id: this.#info.id, name: this.#info.name ?? 'Native repository', remote: this.#info.remote, branch: status.branch, refs: new Map(refs.filter(r => r.name.startsWith('refs/heads/')).map(r => [r.name.slice(11), r.oid])), worktree: new Map(files.map(path => [path, { oid: null, mode: '100644' }])), commits: new Map(history.map(c => [c.oid, c])), stashes: [], conflicts: new Map(this.#status.conflicts.map(p => [p, {}])), merge: status.operation.length ? status.operation : null };
        this.dispatchEvent(new Event('change')); return this.state;
    }
    async #mutate(operation, args) {
        let result;
        try { result = await this.raw(operation, args); }
        catch (error) { try { await this.refresh(); } catch (refreshError) { error.refreshError = refreshError.message; } throw error; }
        try { await this.refresh(); }
        catch (error) { throw new GitError('refresh_failed', 'The Git operation completed, but status refresh failed. Refresh before repeating the operation.', { completed: true, cause: error.message }); }
        return result;
    }
    async read(path, area = 'worktree') { requireValue(area === 'worktree', 'unsupported_area', 'Use native diff to inspect the index or HEAD.'); const result = await this.raw('read', { path }); return { path, bytes: unbase64(result.base64), entry: { oid: result.version, mode: result.mode } }; }
    async write(path, value, { expectedOid } = {}) { const result = await this.#mutate('write', { path, base64: base64(typeof value === 'string' ? utf8(value) : value), expectedVersion: expectedOid }); return result.version; }
    stage(paths) { return this.#mutate('stage', { paths }); }
    unstage(paths) { return this.#mutate('unstage', { paths }); }
    discard(paths, confirm) { return this.#mutate('discard', { paths, confirm }); }
    async diff(path, staged = false) { return { path, staged, native: true, hunks: [], ...(await this.raw('diffview', { path, staged })) }; }
    stageHunks(path, selected, token) { return this.#mutate('stagehunks', { path, selected, token }); }
    async commit(message, author) { return (await this.#mutate('commit', { message, name: author.name, email: author.email })).head; }
    createBranch(branch) { return this.#mutate('branch', { branch }); }
    checkout(branch) { return this.#mutate('checkout', { branch }); }
    deleteBranch(branch, confirm) { return this.#mutate('deletebranch', { branch, confirm }); }
    stash(message) { return this.#mutate('stash', { message }); }
    applyStash(index, pop = false) { return this.#mutate(pop ? 'popstash' : 'applystash', { index }); }
    fetch() { return this.#mutate('fetch'); }
    pull() { return this.#mutate('pull', { branch: this.#state.branch }); }
    push({ create = false } = {}) { return this.#mutate('push', { branch: this.#state.branch, confirm: 'push', upstream: create }); }
    merge(commit) { return this.#mutate('merge', { commit }); }
    async action(operation, args) { return this.#mutate(operation, args); }
}
