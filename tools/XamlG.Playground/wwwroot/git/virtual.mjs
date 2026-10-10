import { GitError, LIMITS, requireValue, repositoryPath, branchName, githubRemote, gitHash, buildTrees, makeCommit, changes, equalEntry, utf8, text, base64, unbase64, oid } from './core.mjs';
import { mergeTrees } from './merge.mjs';
import { topologicalCommits } from './graph.mjs';
import { withWorkspaceLock } from './storage.mjs';
import { diffHunks, applyHunks, unifiedDiff } from './diff.mjs';

/** Persistent browser Git: immutable blobs/commits; transactional HEAD/index/worktree. */
export class VirtualRepository extends EventTarget {
    #store; #api; #state;
    constructor(store, api, state) { super(); this.#store = store; this.#api = api; this.#state = structuredClone(state); }
    get state() { return structuredClone(this.#state); }
    get apiPath() { const r = this.#state.remote; requireValue(r, 'no_remote', 'Configure a GitHub remote first.'); return `/repos/${encodeURIComponent(r.owner)}/${encodeURIComponent(r.repo)}`; }
    static async create(store, api, { name = 'Untitled', branch = 'main' } = {}) {
        branchName(branch);
        const state = { id: crypto.randomUUID(), name, revision: -1, remote: null, branch, refs: new Map([[branch, null]]), tracking: new Map(), head: new Map(), index: new Map(), worktree: new Map(), commits: new Map(), localBlobs: new Set(), stashes: [], conflicts: new Map(), merge: null, pendingPush: null };
        state.revision = await store.save(state, -1); return new VirtualRepository(store, api, state);
    }
    static async load(store, api, id) { const state = await store.load(id); requireValue(state, 'missing_workspace', 'Repository not found.'); return new VirtualRepository(store, api, state); }
    static async clone(store, api, url, branch, { signal } = {}) {
        const remote = githubRemote(url), path = `/repos/${remote.owner}/${remote.repo}`;
        const info = await api.request('GET', path, undefined, { signal }); branch = branchName(branch || info.default_branch);
        const repository = await VirtualRepository.create(store, api, { name: remote.repo, branch });
        try {
            await repository.#mutate(async state => {
                state.remote = remote;
                let ref;
                try { ref = await api.request('GET', `${path}/git/ref/heads/${branch.split('/').map(encodeURIComponent).join('/')}`, undefined, { signal }); }
                catch (error) { if (error.code === 'github_409' && info.size === 0) return; throw error; }
                const commit = await repository.#remoteCommit(ref.object.sha, state, signal);
                state.refs.set(branch, commit.oid); state.tracking.set(branch, commit.oid);
                state.head = new Map(commit.files); state.index = new Map(commit.files); state.worktree = new Map(commit.files);
            });
            return repository;
        } catch (error) { await store.remove(repository.#state.id, repository.#state.revision); throw error; }
    }
    async refresh() { this.#state = await this.#store.load(this.#state.id); requireValue(this.#state, 'missing_workspace', 'Repository was removed.'); this.#notify(); return this.state; }
    #notify() { this.dispatchEvent(new Event('change')); }
    async #fresh() { const current = await this.#store.load(this.#state.id); requireValue(current?.revision === this.#state.revision, 'stale_workspace', 'Repository changed in another tab; refresh before retrying.'); return structuredClone(this.#state); }
    async #persist(state, blobs = []) { state.revision = await this.#store.save(state, this.#state.revision, blobs); this.#state = state; this.#notify(); }
    #mutate(action) { return withWorkspaceLock(this.#state.id, async () => { const next = await this.#fresh(), blobs = []; requireValue(!next.pendingPush, 'pending_push', 'Reconcile the pending push before changing this repository.'); const result = await action(next, blobs); await this.#persist(next, blobs); return result; }); }
    status() { const s = this.#state; return { branch: s.branch, head: s.refs.get(s.branch), staged: changes(s.head, s.index), unstaged: changes(s.index, s.worktree), conflicts: [...s.conflicts.keys()], pendingPush: s.pendingPush }; }
    async setRemote(url) { const remote = githubRemote(url); return this.#mutate(s => { requireValue(!s.remote || s.remote.url === remote.url, 'remote_exists', 'Changing an existing remote requires a separate workspace.'); s.remote = remote; }); }
    async #blob(entry, signal) {
        if (!entry) return new Uint8Array();
        requireValue(entry.mode !== '160000', 'gitlink', 'Submodule entries are commit references, not files.');
        const cached = await this.#store.blob(entry.oid); if (cached) return cached;
        const result = await this.#api.request('GET', `${this.apiPath}/git/blobs/${oid(entry.oid)}`, undefined, { signal });
        requireValue(result.encoding === 'base64' && result.size <= LIMITS.blobBytes, 'blob_limit', 'Blob is too large or has an unsupported encoding.');
        const bytes = unbase64(result.content); requireValue(bytes.length <= LIMITS.blobBytes && await gitHash('blob', bytes) === entry.oid, 'invalid_blob', 'GitHub returned a mismatched blob.');
        await this.#store.cacheBlob(entry.oid, bytes); return bytes;
    }
    async read(path, area = 'worktree', signal) { repositoryPath(path); requireValue(['head', 'index', 'worktree'].includes(area), 'invalid_area', 'Unknown repository area.'); const entry = this.#state[area].get(path); requireValue(entry, 'missing_file', 'File not found.'); return { path, entry: { ...entry }, bytes: await this.#blob(entry, signal), revision: this.#state.revision }; }
    async readAt(commitId, path) {
        oid(commitId); repositoryPath(path); const commit = this.#state.commits.get(commitId);
        requireValue(commit, 'history_required', 'Load this commit first.'); return this.#blob(commit.files.get(path));
    }
    async readConflict(path, side) {
        repositoryPath(path); requireValue(['base', 'ours', 'theirs'].includes(side), 'invalid_side', 'Invalid conflict side.');
        const conflict = this.#state.conflicts.get(path); requireValue(conflict, 'missing_conflict', 'Conflict not found.'); return this.#blob(conflict[side]);
    }
    async write(path, value, { expectedOid, mode } = {}) {
        repositoryPath(path); const bytes = typeof value === 'string' ? utf8(value) : value.slice();
        requireValue(bytes instanceof Uint8Array && bytes.length <= LIMITS.blobBytes, 'blob_limit', 'File exceeds the editing limit.'); const id = await gitHash('blob', bytes);
        return this.#mutate((s, blobs) => {
            const previous = s.worktree.get(path);
            if (expectedOid !== undefined) requireValue((previous?.oid ?? null) === expectedOid, 'stale_file', 'The file changed after this editor opened.');
            requireValue(previous?.mode !== '160000', 'gitlink', 'Use native Git to change submodule references.');
            mode ??= previous?.mode ?? '100644'; requireValue(['100644', '100755', '120000'].includes(mode), 'invalid_mode', 'Unsupported file mode.');
            for (const existing of s.worktree.keys()) requireValue(existing === path || !existing.startsWith(path + '/') && !path.startsWith(existing + '/'), 'path_collision', 'A file shadows a directory.');
            s.worktree.set(path, { oid: id, mode }); s.localBlobs.add(id); blobs.push([id, bytes]); return id;
        });
    }
    remove(path, expectedOid) { repositoryPath(path); return this.#mutate(s => { requireValue(s.worktree.has(path), 'missing_file', 'File not found.'); if (expectedOid !== undefined) requireValue(s.worktree.get(path).oid === expectedOid, 'stale_file', 'File changed.'); s.worktree.delete(path); }); }
    rename(from, to) { repositoryPath(from); repositoryPath(to); return this.#mutate(s => { requireValue(s.worktree.has(from) && !s.worktree.has(to), 'invalid_rename', 'Source is missing or destination exists.'); for (const p of s.worktree.keys()) if (p !== from) requireValue(!p.startsWith(to + '/') && !to.startsWith(p + '/'), 'path_collision', 'A file shadows a directory.'); s.worktree.set(to, s.worktree.get(from)); s.worktree.delete(from); }); }
    stage(paths) { return this.#copy(paths, 'worktree', 'index'); }
    unstage(paths) { return this.#copy(paths, 'head', 'index'); }
    discard(paths, confirmation) { requireValue(confirmation === 'discard', 'confirmation_required', 'Confirm discarding worktree changes.'); return this.#copy(paths, 'index', 'worktree'); }
    #copy(paths, from, to) { paths.forEach(repositoryPath); return this.#mutate(s => { for (const path of paths) { const entry = s[from].get(path); if (entry) s[to].set(path, { ...entry }); else s[to].delete(path); } }); }
    async diff(path, staged = false) {
        repositoryPath(path); const s = this.#state, before = (staged ? s.head : s.index).get(path), after = (staged ? s.index : s.worktree).get(path);
        const token = { before: before?.oid ?? null, after: after?.oid ?? null, revision: s.revision };
        try { const left = text(await this.#blob(before)), right = text(await this.#blob(after)); return { path, staged, ...unifiedDiff(path, left, right), before: left, after: right, token, binary: false }; }
        catch (error) { if (!['binary_file', 'gitlink'].includes(error.code)) throw error; return { path, staged, binary: true, before, after, token, hunks: [], patch: 'Binary or submodule entry changed.' }; }
    }
    stageHunks(path, selected, token) {
        repositoryPath(path); requireValue(Array.isArray(selected) && selected.length > 0, 'invalid_hunk', 'Select at least one hunk.');
        return this.#mutate(async (s, blobs) => {
            const before = s.index.get(path), after = s.worktree.get(path);
            requireValue(token?.revision === s.revision && (before?.oid ?? null) === token.before && (after?.oid ?? null) === token.after, 'stale_hunk', 'Refresh the diff before staging these hunks.');
            const old = text(await this.#blob(before)), next = text(await this.#blob(after));
            const { hunks } = diffHunks(old, next); const value = applyHunks(old, hunks, selected);
            if (selected.length === hunks.length && !after) s.index.delete(path);
            else { const bytes = utf8(value), id = await gitHash('blob', bytes); s.index.set(path, { oid: id, mode: before?.mode ?? after?.mode ?? '100644' }); s.localBlobs.add(id); blobs.push([id, bytes]); }
        });
    }
    async commit(message, author) {
        return this.#mutate(async s => {
            requireValue(s.conflicts.size === 0, 'merge_conflicts', 'Resolve every merge conflict before committing.');
            requireValue(changes(s.head, s.index).length || s.merge, 'nothing_staged', 'Stage changes before committing.');
            const parents = [s.refs.get(s.branch), s.merge?.other].filter(Boolean), tree = await buildTrees(s.index), commit = await makeCommit(tree.oid, [...new Set(parents)], message, author);
            commit.files = new Map(s.index); commit.local = true; s.commits.set(commit.oid, commit); s.refs.set(s.branch, commit.oid); s.head = new Map(s.index); s.merge = null; return commit.oid;
        });
    }
    createBranch(name) { branchName(name); return this.#mutate(s => { requireValue(!s.refs.has(name), 'branch_exists', 'Branch already exists.'); s.refs.set(name, s.refs.get(s.branch)); }); }
    checkout(name) { branchName(name); return this.#mutate(s => { this.#clean(s); requireValue(s.refs.has(name), 'missing_branch', 'Branch does not exist.'); const files = s.commits.get(s.refs.get(name))?.files ?? new Map(); s.branch = name; s.head = new Map(files); s.index = new Map(files); s.worktree = new Map(files); }); }
    deleteBranch(name, confirmation) { branchName(name); return this.#mutate(s => { requireValue(name !== s.branch && confirmation === name, 'confirmation_required', 'Confirm deletion of a non-current branch by its name.'); s.refs.delete(name); }); }
    #clean(s) { requireValue(!s.merge && !s.conflicts.size && !changes(s.head, s.index).length && !changes(s.index, s.worktree).length, 'dirty_worktree', 'Commit or stash changes first.'); }
    stash(message = 'Work in progress') { return this.#mutate(s => { requireValue(!s.merge && !s.conflicts.size, 'merge_in_progress', 'Finish or abort the merge first.'); requireValue(changes(s.head, s.index).length || changes(s.index, s.worktree).length, 'clean_worktree', 'There are no changes to stash.'); const stash = { id: crypto.randomUUID(), message, head: s.refs.get(s.branch), index: new Map(s.index), worktree: new Map(s.worktree) }; s.stashes.unshift(stash); s.index = new Map(s.head); s.worktree = new Map(s.head); return stash.id; }); }
    applyStash(id, pop = false) { return this.#mutate(async (s, blobs) => {
        this.#clean(s); const stash = s.stashes.find(x => x.id === id); requireValue(stash, 'missing_stash', 'Stash not found.');
        if (stash.head === s.refs.get(s.branch)) { s.index = new Map(stash.index); s.worktree = new Map(stash.worktree); }
        else {
            const base = stash.head ? s.commits.get(stash.head)?.files : new Map();
            requireValue(base, 'history_required', 'Load the stash base before restoring it.');
            const index = await this.#mergeTrees(base, s.head, stash.index, s, blobs);
            requireValue(!index.conflicts.size, 'stash_conflicts', 'The staged stash changes conflict with this branch. The stash and working tree are unchanged; use native Git to resolve.');
            const work = await this.#mergeTrees(stash.index, index.files, stash.worktree, s, blobs);
            requireValue(!work.conflicts.size, 'stash_conflicts', 'The working stash changes conflict with this branch. The stash and working tree are unchanged; use native Git to resolve.');
            s.index = index.files; s.worktree = work.files;
        }
        if (pop) s.stashes = s.stashes.filter(x => x.id !== id);
    }); }
    dropStash(id, confirmation) { return this.#mutate(s => { requireValue(confirmation === id, 'confirmation_required', 'Confirm the stash ID before dropping it.'); s.stashes = s.stashes.filter(x => x.id !== id); }); }
    async #remoteCommit(id, state, signal) {
        oid(id); if (state.commits.has(id)) return state.commits.get(id);
        const path = `/repos/${state.remote.owner}/${state.remote.repo}`, c = await this.#api.request('GET', `${path}/git/commits/${id}`, undefined, { signal });
        const root = oid(c.tree.sha), files = new Map();
        const add = (entry, prefix = '') => { const name = repositoryPath(prefix + entry.path); requireValue(files.size < LIMITS.files, 'tree_limit', 'Repository exceeds the file limit.'); requireValue(['100644', '100755', '120000', '160000'].includes(entry.mode), 'invalid_mode', 'Unsupported tree mode.'); requireValue(!files.has(name), 'duplicate_path', 'Duplicate tree entry.'); files.set(name, { oid: oid(entry.sha), mode: entry.mode }); };
        const tree = await this.#api.request('GET', `${path}/git/trees/${root}?recursive=1`, undefined, { signal });
        if (!tree.truncated) { for (const entry of tree.tree) if (entry.type !== 'tree') add(entry); }
        else {
            const queue = [[root, '', new Set()]]; let count = 0;
            for (let index = 0; index < queue.length; index++) {
                const [sha, prefix, ancestors] = queue[index]; requireValue(!ancestors.has(sha) && ++count <= LIMITS.files, 'tree_limit', 'Tree nesting or size exceeds the limit.');
                const part = await this.#api.request('GET', `${path}/git/trees/${sha}`, undefined, { signal }); requireValue(!part.truncated, 'tree_limit', 'A directory exceeds the API tree limit.');
                for (const entry of part.tree) { if (entry.type === 'tree') { repositoryPath(prefix + entry.path); queue.push([oid(entry.sha), prefix + entry.path + '/', new Set([...ancestors, sha])]); } else add(entry, prefix); }
            }
        }
        const computed = await buildTrees(files); requireValue(computed.oid === root, 'invalid_tree', 'Repository tree hash did not verify.');
        const result = { oid: id, tree: root, parents: c.parents.map(p => oid(p.sha)), message: c.message, author: c.author, committer: c.committer, files, local: false };
        state.commits.set(id, result); return result;
    }
    fetch(branch = this.#state.branch, options = {}) {
        branchName(branch); return this.#mutate(async s => {
            const ref = await this.#api.request('GET', `${this.apiPath}/git/ref/heads/${branch.split('/').map(encodeURIComponent).join('/')}`, undefined, options);
            const commit = await this.#remoteCommit(ref.object.sha, s, options.signal); s.tracking.set(branch, commit.oid); return commit.oid;
        });
    }
    pull(options = {}) { return this.#mutate(async s => {
        this.#clean(s); const branch = s.branch;
        const ref = await this.#api.request('GET', `${this.apiPath}/git/ref/heads/${branch.split('/').map(encodeURIComponent).join('/')}`, undefined, options);
        const other = await this.#remoteCommit(ref.object.sha, s, options.signal), current = s.refs.get(branch);
        if (current && current !== other.oid) {
            const comparison = await this.#api.request('GET', `${this.apiPath}/compare/${current}...${other.oid}`, undefined, options);
            requireValue(comparison.status === 'ahead', 'non_fast_forward', 'Branches diverged. Fetch and merge explicitly; pull never discards local commits.');
        }
        s.refs.set(branch, other.oid); s.tracking.set(branch, other.oid); s.head = new Map(other.files); s.index = new Map(other.files); s.worktree = new Map(other.files); return other.oid;
    }); }
    history(limit = 100) {
        requireValue(Number.isInteger(limit) && limit > 0 && limit <= 1000, 'invalid_limit', 'Invalid history limit.');
        const result = [], seen = new Set(), queue = [this.#state.refs.get(this.#state.branch)];
        for (let at = 0; at < queue.length && result.length < limit; at++) {
            const id = queue[at]; if (!id || seen.has(id)) continue; seen.add(id);
            const commit = this.#state.commits.get(id);
            if (!commit) { result.push({ oid: id, unloaded: true }); continue; }
            result.push(structuredClone(commit)); queue.push(...commit.parents);
        }
        return topologicalCommits(result);
    }
    loadHistory(limit = 50, signal) {
        requireValue(Number.isInteger(limit) && limit > 0 && limit <= 1000, 'invalid_limit', 'Invalid history limit.');
        return this.#mutate(async s => {
            const queue = [s.refs.get(s.branch)], seen = new Set(); let loaded = 0;
            for (let i = 0; i < queue.length; i++) {
                const id = queue[i]; if (!id || seen.has(id)) continue;
                requireValue(seen.size < LIMITS.files, 'history_limit', 'History traversal exceeds the limit.'); seen.add(id);
                let commit = s.commits.get(id);
                if (!commit) { if (loaded >= limit) break; commit = await this.#remoteCommit(id, s, signal); loaded++; }
                queue.push(...commit.parents);
            }
            return { loaded };
        });
    }
    merge(otherId) { oid(otherId); return this.#mutate(async (s, blobs) => {
        this.#clean(s); const oursId = s.refs.get(s.branch), theirs = s.commits.get(otherId); requireValue(theirs, 'history_required', 'Fetch the target commit first.');
        const ancestors = start => { const seen = new Set(), q = [start]; for (let i = 0; i < q.length; i++) { const id = q[i]; if (!id || seen.has(id)) continue; seen.add(id); const c = s.commits.get(id); requireValue(c, 'history_required', 'Load more history before merging.'); q.push(...c.parents); } return seen; };
        const ours = ancestors(oursId), other = ancestors(otherId);
        if (ours.has(otherId)) return { upToDate: true };
        if (!oursId || other.has(oursId)) { s.refs.set(s.branch, otherId); s.head = new Map(theirs.files); s.index = new Map(theirs.files); s.worktree = new Map(theirs.files); return { fastForward: true }; }
        const common = [...ours].filter(id => other.has(id));
        // Reject criss-cross history rather than selecting an incorrect merge base.
        // Common ancestors are closed under parent traversal. Marking the parents of
        // common nodes removes every non-best base in O(V + E), without repeated DFS.
        const nonBest = new Set(common.flatMap(id => s.commits.get(id).parents));
        const bases = common.filter(id => !nonBest.has(id));
        requireValue(bases.length === 1, 'merge_base', 'Use native Git for unrelated or multiple-base merges.');
        const merged = await this.#mergeTrees(s.commits.get(bases[0]).files, s.head, theirs.files, s, blobs);
        s.merge = { other: otherId, original: new Map(s.head) }; s.conflicts = merged.conflicts;
        s.index = new Map(merged.files); s.worktree = new Map(merged.files); return { conflicts: [...s.conflicts.keys()] };
    }); }
    #mergeTrees(base, ours, theirs, state, blobs) {
        const pending = new Map(blobs);
        return mergeTrees(base, ours, theirs, {
            readText: async entry => text(pending.get(entry.oid) ?? await this.#blob(entry)),
            writeText: async (value, mode) => {
                const bytes = utf8(value); requireValue(bytes.length <= LIMITS.blobBytes, 'blob_limit', 'Merged content exceeds the file size limit.');
                const id = await gitHash('blob', bytes);
                state.localBlobs.add(id); pending.set(id, bytes); blobs.push([id, bytes]); return { oid: id, mode };
            }
        });
    }
    resolve(path, side = 'worktree') { repositoryPath(path); return this.#mutate(s => { const conflict = s.conflicts.get(path); requireValue(conflict, 'missing_conflict', 'Conflict not found.'); requireValue(['ours', 'theirs', 'base', 'worktree'].includes(side), 'invalid_side', 'Invalid resolution.'); const entry = side === 'worktree' ? s.worktree.get(path) : conflict[side]; if (entry) { s.worktree.set(path, entry); s.index.set(path, entry); } else { s.worktree.delete(path); s.index.delete(path); } s.conflicts.delete(path); }); }
    abortMerge(confirmation) { return this.#mutate(s => { requireValue(s.merge && confirmation === 'abort', 'confirmation_required', 'Confirm discarding merge resolutions.'); s.index = new Map(s.merge.original); s.worktree = new Map(s.merge.original); s.merge = null; s.conflicts.clear(); }); }
    async #upload(id, state, mapped, blobs, options) {
        if (mapped.has(id)) return mapped.get(id);
        const commit = state.commits.get(id); if (!commit?.local) return id;
        const parents = []; for (const parent of commit.parents) parents.push(await this.#upload(parent, state, mapped, blobs, options));
        for (const entry of commit.files.values()) if (entry.mode !== '160000' && state.localBlobs.has(entry.oid) && !blobs.has(entry.oid)) {
            const bytes = await this.#store.blob(entry.oid); requireValue(bytes, 'missing_blob', 'A local blob is missing.');
            const result = await this.#api.request('POST', `${this.apiPath}/git/blobs`, { content: base64(bytes), encoding: 'base64' }, options); requireValue(result.sha === entry.oid, 'invalid_blob', 'Uploaded blob hash differs.'); blobs.add(entry.oid);
        }
        const tree = await buildTrees(commit.files);
        for (const object of tree.objects) {
            const result = await this.#api.request('POST', `${this.apiPath}/git/trees`, { tree: object.entries.map(e => ({ path: e.name, mode: e.mode === '40000' ? '040000' : e.mode, type: e.mode === '40000' ? 'tree' : e.mode === '160000' ? 'commit' : 'blob', sha: e.oid })) }, options);
            requireValue(result.sha === object.oid, 'invalid_tree', 'Uploaded tree hash differs.');
        }
        const result = await this.#api.request('POST', `${this.apiPath}/git/commits`, { message: commit.message, tree: tree.oid, parents, author: commit.author, committer: commit.committer }, options);
        mapped.set(id, oid(result.sha)); return result.sha;
    }
    /** Journal before mutating a remote ref. Ambiguous responses never trigger an automatic retry. */
    push({ create = false, ...options } = {}) { return withWorkspaceLock(this.#state.id, async () => {
        const s = await this.#fresh(); requireValue(!s.pendingPush && !s.merge, 'operation_in_progress', 'Finish the pending Git operation first.');
        const head = s.refs.get(s.branch); requireValue(head, 'no_commits', 'Create a commit first.');
        const path = `${this.apiPath}/git/ref/heads/${s.branch.split('/').map(encodeURIComponent).join('/')}`; let remote = null;
        try { remote = (await this.#api.request('GET', path, undefined, options)).object.sha; }
        catch (error) {
            if (error.code === 'github_409') throw new GitError('empty_remote', 'Initialize this empty GitHub repository through native Git before publishing virtual branches.');
            if (error.code !== 'github_404') throw error;
        }
        if (remote === head) return head;
        const expected = s.tracking.get(s.branch) ?? null;
        requireValue(remote === expected && (remote || create), 'non_fast_forward', 'Remote changed or is not tracked. Fetch and integrate it, or explicitly publish a new branch.');
        // Ensure publication contains the tracked base, even if the branch was switched locally.
        const reachable = new Set(), queue = [head]; for (let i = 0; i < queue.length; i++) { const id = queue[i]; if (reachable.has(id)) continue; reachable.add(id); queue.push(...(s.commits.get(id)?.parents ?? [])); }
        requireValue(!remote || reachable.has(remote), 'non_fast_forward', 'The local branch does not contain the remote base.');
        const mapped = new Map(), target = await this.#upload(head, s, mapped, new Set(), options);
        s.pendingPush = { branch: s.branch, expected: remote, target, mapped, create: !remote }; await this.#persist(s);
        try {
            if (remote) await this.#api.request('PATCH', `${this.apiPath}/git/refs/heads/${s.branch.split('/').map(encodeURIComponent).join('/')}`, { sha: target, force: false }, options);
            else await this.#api.request('POST', `${this.apiPath}/git/refs`, { ref: `refs/heads/${s.branch}`, sha: target }, options);
        } catch (error) { throw new GitError('push_uncertain', 'The push was not confirmed. Use Reconcile push before retrying.', { cause: error.code }); }
        await this.#finishPush(structuredClone(this.#state)); return target;
    }); }
    async #finishPush(s) {
        const pending = s.pendingPush;
        for (const [oldId, newId] of pending.mapped) { const c = s.commits.get(oldId); s.commits.set(newId, { ...c, oid: newId, parents: c.parents.map(p => pending.mapped.get(p) ?? p), local: false }); }
        for (const [name, id] of s.refs) s.refs.set(name, pending.mapped.get(id) ?? id);
        for (const stash of s.stashes) stash.head = pending.mapped.get(stash.head) ?? stash.head;
        s.tracking.set(pending.branch, pending.target); s.pendingPush = null; await this.#persist(s);
    }
    reconcilePush(options = {}) { return withWorkspaceLock(this.#state.id, async () => {
        const s = await this.#fresh(), p = s.pendingPush; requireValue(p, 'no_pending_push', 'No push needs reconciliation.'); let remote = null;
        try { remote = (await this.#api.request('GET', `${this.apiPath}/git/ref/heads/${p.branch.split('/').map(encodeURIComponent).join('/')}`, undefined, options)).object.sha; } catch (error) { if (!['github_404', 'github_409'].includes(error.code)) throw error; }
        if (remote === p.target) { await this.#finishPush(s); return 'published'; }
        if (remote === p.expected) { s.pendingPush = null; await this.#persist(s); return 'not-published'; }
        throw new GitError('remote_diverged', 'Remote moved to another commit. Pending publication is retained; inspect the remote before recovery.');
    }); }
}
