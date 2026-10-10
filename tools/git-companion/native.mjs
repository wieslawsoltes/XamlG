import { spawn } from 'node:child_process';
import { constants } from 'node:fs';
import { mkdir, mkdtemp, readdir, readFile, writeFile, lstat, realpath, rm, rename, open, chmod } from 'node:fs/promises';
import { join, dirname, resolve, relative, sep, isAbsolute } from 'node:path';
import { randomUUID, createHash } from 'node:crypto';
import { GitError, LIMITS, SerialQueue, requireValue, repositoryPath, branchName, httpsRemote, text } from '../XamlG.Playground/wwwroot/git/core.mjs';
import { unifiedDiff, applyHunks } from '../XamlG.Playground/wwwroot/git/diff.mjs';

export function parseStatus(output) {
    const values = output.split('\0'), entries = [];
    for (let i = 0; i < values.length; i++) {
        const row = values[i]; if (!row) continue;
        const entry = { code: row.slice(0, 2), path: row.slice(3), original: null };
        if (/[RC]/.test(entry.code)) entry.original = values[++i];
        entry.conflict = ['DD', 'AU', 'UD', 'UA', 'DU', 'AA', 'UU'].includes(entry.code);
        entries.push(entry);
    }
    return entries;
}
const bytesHash = bytes => createHash('sha256').update(bytes).digest('hex');
const validId = id => requireValue(typeof id === 'string' && /^[a-f0-9-]{36}$/.test(id), 'invalid_repository', 'Invalid managed repository ID.');

/** Private managed repositories only. No shell, inherited Git configuration, hooks, or public proxy. */
export class NativeGit {
    #root; #home; #hooks; #queues = new Map(); #credentials = new Map(); #hosts; #ready;
    constructor(root, { hosts = ['github.com'], timeout = 120000, outputLimit = LIMITS.responseBytes, caFile } = {}) {
        this.#root = resolve(root); this.#home = join(this.#root, 'home'); this.#hooks = join(this.#root, 'empty-hooks');
        this.#hosts = [...hosts]; this.timeout = timeout; this.outputLimit = outputLimit; this.caFile = caFile;
        this.#ready = this.#initialize();
    }
    async #initialize() {
        await mkdir(this.#root, { recursive: true, mode: 0o700 });
        requireValue(!(await lstat(this.#root)).isSymbolicLink(), 'unsafe_root', 'The managed root must not be a symlink.');
        // Canonicalize ancestor aliases (for example macOS /var -> /private/var),
        // while refusing a symlink as the managed root itself.
        this.#root = await realpath(this.#root);
        this.#home = join(this.#root, 'home'); this.#hooks = join(this.#root, 'empty-hooks');
        await chmod(this.#root, 0o700);
        for (const path of [this.#home, this.#hooks, join(this.#root, 'repos')]) { await mkdir(path, { recursive: true, mode: 0o700 }); requireValue(!(await lstat(path)).isSymbolicLink(), 'unsafe_root', 'Managed directories must not be symlinks.'); }
    }
    setCredential(host, username, password) {
        const url = new URL(host.includes('://') ? host : `https://${host}`);
        requireValue(url.protocol === 'https:' && !url.username && !url.password && url.pathname === '/' && !url.search && !url.hash && this.#hosts.includes(url.hostname) && typeof username === 'string' && typeof password === 'string' && !/[\r\n\0:]/.test(username) && !/[\r\n\0]/.test(password), 'invalid_credentials', 'Invalid credential host or values.');
        this.#credentials.set(url.origin, { username, password });
    }
    clearCredentials() { this.#credentials.clear(); }
    async list() { await this.#ready; const entries = await readdir(join(this.#root, 'repos'), { withFileTypes: true }); const result = []; for (const entry of entries) if (entry.isDirectory() && /^[a-f0-9-]{36}$/.test(entry.name)) { try { result.push({ id: entry.name, ...JSON.parse(await readFile(join(this.#root, 'repos', entry.name, 'workspace.json'), 'utf8')) }); } catch {} } return result; }
    async #location(id) { await this.#ready; validId(id); const root = join(this.#root, 'repos', id), repo = join(root, 'worktree'); requireValue(await realpath(root) === root && await realpath(repo) === repo, 'unsafe_repository', 'Repository path must not contain symlinks.'); return repo; }
    async #allocate(name, remote) {
        await this.#ready; const id = randomUUID(), root = join(this.#root, 'repos', id); await mkdir(root, { mode: 0o700 });
        await writeFile(join(root, 'workspace.json'), JSON.stringify({ name, remote }), { mode: 0o600 }); return { id, root, repo: join(root, 'worktree') };
    }
    #environment(remote) {
        // Inherit only platform process essentials, never GIT_*, proxy, SSH, pager or filter settings.
        const env = { PATH: process.env.PATH, SystemRoot: process.env.SystemRoot, WINDIR: process.env.WINDIR, TEMP: process.env.TEMP, TMP: process.env.TMP,
            HOME: this.#home, USERPROFILE: this.#home, XDG_CONFIG_HOME: this.#home, LC_ALL: 'C.UTF-8', GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_SYSTEM: '/dev/null', GIT_CONFIG_GLOBAL: '/dev/null',
            GIT_SSL_CAINFO: this.caFile, GIT_TERMINAL_PROMPT: '0', GIT_PAGER: 'cat', GIT_OPTIONAL_LOCKS: '0' };
        const settings = [ ['core.hooksPath', this.#hooks], ['init.templateDir', this.#hooks], ['credential.helper', ''], ['credential.interactive', 'false'], ['core.askPass', ''], ['core.fsmonitor', 'false'], ['core.untrackedCache', 'false'], ['core.quotePath', 'false'], ['protocol.allow', 'never'], ['protocol.https.allow', 'always'], ['http.followRedirects', 'false'], ['submodule.recurse', 'false'], ['fetch.recurseSubmodules', 'false'], ['commit.gpgSign', 'false'], ['tag.gpgSign', 'false'], ['core.editor', 'true'], ['sequence.editor', 'true'] ];
        if (remote) {
            const url = httpsRemote(remote, this.#hosts), credential = this.#credentials.get(url.origin);
            if (credential) settings.push([`http.${url.origin}/.extraHeader`, `Authorization: Basic ${Buffer.from(`${credential.username}:${credential.password}`).toString('base64')}`]);
        }
        env.GIT_CONFIG_COUNT = String(settings.length); settings.forEach(([key, value], index) => { env[`GIT_CONFIG_KEY_${index}`] = key; env[`GIT_CONFIG_VALUE_${index}`] = value; });
        return Object.fromEntries(Object.entries(env).filter(([, value]) => value !== undefined));
    }
    async #exec(cwd, args, { remote, signal, input, allowFailure = false } = {}) {
        const result = await new Promise((resolvePromise, reject) => {
            const child = spawn('git', ['--no-pager', ...args], { cwd, env: this.#environment(remote), shell: false, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
            const stdout = [], stderr = []; let size = 0, failure;
            const abort = () => { failure ??= new GitError('aborted', 'Git operation canceled. Refresh repository status before retrying.'); child.kill('SIGKILL'); };
            const timer = setTimeout(() => { failure ??= new GitError('timeout', 'Git operation timed out. Refresh before retrying.'); child.kill('SIGKILL'); }, this.timeout);
            signal?.addEventListener('abort', abort, { once: true }); if (signal?.aborted) abort();
            const collect = target => data => { size += data.length; if (size > this.outputLimit) { failure ??= new GitError('output_limit', 'Git output exceeds the configured limit.'); child.kill('SIGKILL'); } else target.push(data); };
            child.stdout.on('data', collect(stdout)); child.stderr.on('data', collect(stderr)); child.stdin.on('error', () => {});
            child.on('error', error => { failure ??= error; });
            child.on('close', code => { clearTimeout(timer); signal?.removeEventListener('abort', abort); if (failure) reject(failure); else resolvePromise({ code, stdout: Buffer.concat(stdout), stderr: Buffer.concat(stderr).toString('utf8') }); });
            child.stdin.end(input);
        });
        if (result.code !== 0 && !allowFailure) throw new GitError('git_failed', result.stderr.slice(0, 4096) || `Git exited with ${result.code}.`, { exitCode: result.code });
        return result;
    }
    #lock(id, action) { if (!this.#queues.has(id)) this.#queues.set(id, new SerialQueue()); return this.#queues.get(id).run(action); }
    async init({ name = 'Local repository', branch = 'main' } = {}, signal) {
        branchName(branch); const allocated = await this.#allocate(String(name).slice(0, 200), null);
        try { await this.#exec(allocated.root, ['init', '--initial-branch', branch, '--', allocated.repo], { signal }); return { id: allocated.id, name }; }
        catch (error) { await rm(allocated.root, { recursive: true, force: true }); throw error; }
    }
    async clone({ url, branch, depth } = {}, signal) {
        const remote = httpsRemote(url, this.#hosts).href; if (branch) branchName(branch);
        requireValue(depth === undefined || Number.isInteger(depth) && depth > 0 && depth <= 1000000, 'invalid_depth', 'Invalid clone depth.');
        const allocated = await this.#allocate(new URL(remote).pathname.split('/').at(-1).replace(/\.git$/, ''), remote);
        try { await this.#exec(allocated.root, ['clone', '--no-recurse-submodules', ...(branch ? ['--branch', branch] : []), ...(depth ? ['--depth', String(depth)] : []), '--', remote, allocated.repo], { remote, signal }); return { id: allocated.id, remote }; }
        catch (error) { await rm(allocated.root, { recursive: true, force: true }); throw error; }
    }
    async #safeFile(repo, path, create = false) {
        repositoryPath(path); const full = resolve(repo, path), rel = relative(repo, full);
        requireValue(rel && !rel.startsWith('..' + sep) && !isAbsolute(rel), 'invalid_path', 'Path leaves the repository.');
        let cursor = repo; const parts = path.split('/');
        for (let i = 0; i < parts.length; i++) {
            cursor = join(cursor, parts[i]);
            let stat; try { stat = await lstat(cursor); } catch (error) {
                if (error.code !== 'ENOENT' || !create) throw error;
                if (i < parts.length - 1) await mkdir(cursor, { mode: 0o700 }); continue;
            }
            requireValue(!stat.isSymbolicLink() && (i === parts.length - 1 ? stat.isFile() : stat.isDirectory()), 'unsafe_path', 'Editing through symlinks, devices or submodules is not allowed.');
        }
        return full;
    }
    async #read(repo, path) {
        const full = await this.#safeFile(repo, path), handle = await open(full, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
        try { const info = await handle.stat(); requireValue(info.isFile() && info.size <= LIMITS.blobBytes, 'blob_limit', 'File is not regular or is too large.'); const bytes = await handle.readFile(); requireValue(bytes.length <= LIMITS.blobBytes, 'blob_limit', 'File grew beyond the limit.'); return { path, base64: bytes.toString('base64'), version: bytesHash(bytes), mode: info.mode & 0o111 ? '100755' : '100644' }; }
        finally { await handle.close(); }
    }
    async #revision(repo, value, signal) {
        requireValue(typeof value === 'string' && value.length <= 1024 && (value === 'HEAD' || /^[a-f0-9]{40}$/.test(value) || branchName(value)), 'invalid_revision', 'Invalid revision.');
        const result = await this.#exec(repo, ['rev-parse', '--verify', '--end-of-options', `${value}^{commit}`], { signal }); return result.stdout.toString('utf8').trim();
    }
    async #remote(repo, name, signal) {
        requireValue(typeof name === 'string' && /^[A-Za-z0-9_.-]+$/.test(name) && !name.startsWith('-'), 'invalid_remote', 'Invalid remote name.');
        const value = (await this.#exec(repo, ['remote', 'get-url', '--', name], { signal })).stdout.toString('utf8').trim(); return httpsRemote(value, this.#hosts).href;
    }
    async #status(repo, run) {
        const status = await run(['status', '--porcelain=v1', '-z', '--untracked-files=all']);
        const current = await run(['symbolic-ref', '--quiet', '--short', 'HEAD'], { allowFailure: true });
        const head = await run(['rev-parse', '--verify', 'HEAD'], { allowFailure: true });
        const state = await Promise.all(['MERGE_HEAD', 'rebase-merge', 'rebase-apply', 'CHERRY_PICK_HEAD', 'REVERT_HEAD'].map(async name => {
            try { await lstat(join(repo, '.git', name)); return name; } catch (error) { if (error.code !== 'ENOENT') throw error; return null; }
        }));
        return { entries: parseStatus(status.stdout.toString('utf8')), branch: current.stdout.toString('utf8').trim() || '(detached)', head: head.code === 0 ? head.stdout.toString('utf8').trim() : null, operation: state.filter(Boolean) };
    }
    async #files(run) {
        const tracked = await run(['ls-files', '-z', '--cached', '--others', '--exclude-standard']);
        return [...new Set(tracked.stdout.toString('utf8').split('\0').filter(Boolean))].sort();
    }
    async #branches(run) {
        const values = (await run(['for-each-ref', '--format=%(refname)%00%(objectname)%00%(upstream:short)%00%(HEAD)', 'refs/heads', 'refs/remotes', 'refs/tags'])).stdout.toString('utf8').trim();
        return values ? values.split('\n').map(row => { const [name, oid, upstream, head] = row.split('\0'); return { name, oid, upstream, current: head === '*' }; }) : [];
    }
    async #log(run, args = {}) {
        const count = args.limit ?? 100; requireValue(Number.isInteger(count) && count > 0 && count <= 1000, 'invalid_limit', 'History limit must be 1–1000.');
        const result = await run(['log', '--topo-order', '-z', `--max-count=${count}`, '--date=iso-strict', '--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%s', ...(args.all ? ['--all'] : []), '--'], { allowFailure: true });
        if (result.code && /does not have any commits/.test(result.stderr)) return [];
        if (result.code) throw new GitError('git_failed', result.stderr);
        const fields = result.stdout.toString('utf8').split('\0'), commits = [];
        for (let i = 0; i + 5 < fields.length; i += 6) commits.push({ oid: fields[i], parents: fields[i + 1].split(' ').filter(Boolean), author: { name: fields[i + 2], email: fields[i + 3], date: fields[i + 4] }, message: fields[i + 5] });
        return commits;
    }
    async #objectEntry(run, path, area) {
        const literal = `:(literal)${repositoryPath(path)}`; let entry;
        if (area === 'index') {
            const records = (await run(['ls-files', '--stage', '-z', '--', literal])).stdout.toString('utf8').split('\0').filter(Boolean);
            for (const record of records) {
                const tab = record.indexOf('\t'); if (record.slice(tab + 1) !== path) continue;
                const [mode, oid, stage] = record.slice(0, tab).split(' ');
                requireValue(stage === '0', 'unmerged_index', 'Resolve index conflicts before staging hunks.'); entry = { mode, oid };
            }
        } else {
            const head = await run(['rev-parse', '--verify', 'HEAD'], { allowFailure: true });
            if (head.code !== 0) return null;
            const records = (await run(['ls-tree', '-z', head.stdout.toString('utf8').trim(), '--', literal])).stdout.toString('utf8').split('\0').filter(Boolean);
            for (const record of records) {
                const tab = record.indexOf('\t'); if (record.slice(tab + 1) !== path) continue;
                const [mode, , oid] = record.slice(0, tab).split(' '); entry = { mode, oid };
            }
        }
        if (!entry) return null;
        requireValue(['100644', '100755'].includes(entry.mode), 'non_text_entry', 'Use the Git patch view for symlinks and submodules.');
        const result = await run(['cat-file', 'blob', entry.oid]);
        requireValue(result.stdout.length <= LIMITS.blobBytes, 'blob_limit', 'The Git blob exceeds the editor limit.');
        return { ...entry, bytes: result.stdout, version: entry.oid };
    }
    async #diffView(repo, run, path, staged) {
        repositoryPath(path);
        try {
            const before = await this.#objectEntry(run, path, staged ? 'head' : 'index'); let after;
            if (staged) after = await this.#objectEntry(run, path, 'index');
            else {
                try { const file = await this.#read(repo, path); after = { ...file, bytes: Buffer.from(file.base64, 'base64') }; }
                catch (error) { if (error.code !== 'ENOENT') throw error; after = null; }
            }
            const left = text(before?.bytes ?? new Uint8Array()), right = text(after?.bytes ?? new Uint8Array());
            return { ...unifiedDiff(path, left, right), path, staged, binary: false, before: left, after: right,
                token: { before: before?.version ?? null, after: after?.version ?? null, beforeMode: before?.mode ?? null, afterMode: after?.mode ?? null } };
        } catch (error) {
            if (!['binary_file', 'non_text_entry', 'unmerged_index', 'unsafe_path'].includes(error.code)) throw error;
            const patch = (await run(['diff', '--no-ext-diff', '--no-textconv', '--binary', ...(staged ? ['--cached'] : []), '--', `:(literal)${path}`])).stdout.toString('utf8');
            return { path, staged, binary: true, hunks: [], patch: patch || 'Binary, symlink or unmerged entry. Stage the complete file.', reason: error.code };
        }
    }
    async execute(id, operation, args = {}, signal) {
        return this.#lock(id, async () => {
            const repo = await this.#location(id), run = (argv, options) => this.#exec(repo, argv, { signal, ...options });
            const paths = () => { requireValue(Array.isArray(args.paths) && args.paths.length > 0 && args.paths.length <= 10000, 'invalid_paths', 'Select bounded repository paths.'); return args.paths.map(path => `:(literal)${repositoryPath(path)}`); };
            const confirm = value => requireValue(args.confirm === value, 'confirmation_required', `Confirm ${value} before proceeding.`);
            const branch = () => branchName(args.branch);
            switch (operation) {
                case 'status': return this.#status(repo, run);
                case 'files': return this.#files(run);
                case 'snapshot': {
                    const [status, files, refs, history, stashes] = await Promise.all([
                        this.#status(repo, run), this.#files(run), this.#branches(run), this.#log(run, args),
                        run(['stash', 'list', '--format=%gd %H %s']).then(result => ({ text: result.stdout.toString('utf8') }))
                    ]);
                    return { status, files, refs, history, stashes };
                }
                case 'diffview': return this.#diffView(repo, run, args.path, args.staged === true);
                case 'stagehunks': {
                    requireValue(Array.isArray(args.selected) && args.selected.length > 0, 'invalid_hunk', 'Select at least one hunk.');
                    const diff = await this.#diffView(repo, run, args.path, false);
                    requireValue(!diff.binary && args.token && ['before', 'after', 'beforeMode', 'afterMode'].every(key => args.token[key] === diff.token[key]), 'stale_hunk', 'The index or working file changed. Refresh the diff before staging.');
                    const value = applyHunks(diff.before, diff.hunks, args.selected);
                    if (diff.token.after === null && args.selected.length === diff.hunks.length) {
                        await run(['update-index', '--force-remove', '--', repositoryPath(args.path)]);
                    } else {
                        const object = (await run(['hash-object', '-w', '--stdin'], { input: Buffer.from(value, 'utf8') })).stdout.toString('utf8').trim();
                        await run(['update-index', '--add', '--cacheinfo', diff.token.beforeMode ?? diff.token.afterMode ?? '100644', object, repositoryPath(args.path)]);
                    }
                    return { ok: true };
                }
                case 'read': return this.#read(repo, args.path);
                case 'write': {
                    requireValue(typeof args.base64 === 'string' && args.base64.length <= LIMITS.blobBytes * 1.4, 'blob_limit', 'Invalid file content.');
                    const bytes = Buffer.from(args.base64, 'base64'); requireValue(bytes.toString('base64') === args.base64, 'invalid_base64', 'Use canonical base64 file content.'); requireValue(bytes.length <= LIMITS.blobBytes, 'blob_limit', 'File exceeds the limit.');
                    let previous; try { previous = await this.#read(repo, args.path); } catch (error) { if (error.code !== 'ENOENT') throw error; }
                    requireValue(args.expectedVersion !== undefined && (previous?.version ?? null) === args.expectedVersion, 'stale_file', 'File changed after this editor opened.');
                    const full = await this.#safeFile(repo, args.path, true), temporary = join(dirname(full), `.xamlg-${randomUUID()}.tmp`);
                    try { await writeFile(temporary, bytes, { flag: 'wx', mode: previous?.mode === '100755' ? 0o755 : 0o644 }); await rename(temporary, full); } finally { await rm(temporary, { force: true }); }
                    return { version: bytesHash(bytes) };
                }
                case 'delete': { confirm('delete'); await this.#safeFile(repo, args.path); const current = await this.#read(repo, args.path); requireValue(current.version === args.expectedVersion, 'stale_file', 'File changed.'); await rm(join(repo, args.path)); return {}; }
                case 'stage': await run(['add', '--', ...paths()]); break;
                case 'unstage': { const head = await run(['rev-parse', '--verify', 'HEAD'], { allowFailure: true }); await run(head.code === 0 ? ['reset', '-q', 'HEAD', '--', ...paths()] : ['rm', '--cached', '--ignore-unmatch', '--', ...paths()]); break; }
                case 'discard': {
                    confirm('discard'); paths(); const tracked = [], untracked = [];
                    for (const path of args.paths) {
                        const entry = await run(['ls-files', '--error-unmatch', '--', `:(literal)${path}`], { allowFailure: true });
                        if (entry.code === 0) tracked.push(`:(literal)${path}`);
                        else untracked.push(await this.#safeFile(repo, path));
                    }
                    if (tracked.length) await run(['restore', '--worktree', '--', ...tracked]);
                    for (const path of untracked) await rm(path);
                    break;
                }
                case 'diff': {
                    if (args.path && !args.staged && !args.from && !args.to) {
                        repositoryPath(args.path);
                        const tracked = await run(['ls-files', '--error-unmatch', '--', `:(literal)${args.path}`], { allowFailure: true });
                        if (tracked.code !== 0) {
                            const file = await this.#read(repo, args.path);
                            try { return unifiedDiff(args.path, '', text(Buffer.from(file.base64, 'base64'))); }
                            catch (error) { if (error.code !== 'binary_file') throw error; return { binary: true, patch: 'New binary file. Stage it to inspect the native Git binary patch.' }; }
                        }
                    }
                    const argv = ['diff', '--no-ext-diff', '--no-textconv', '--binary']; if (args.staged) argv.push('--cached');
                    if (args.from) argv.push(await this.#revision(repo, args.from, signal)); if (args.to) argv.push(await this.#revision(repo, args.to, signal));
                    argv.push('--'); if (args.path) argv.push(`:(literal)${repositoryPath(args.path)}`);
                    return { patch: (await run(argv)).stdout.toString('utf8') };
                }
                case 'patch': {
                    requireValue(typeof args.patch === 'string' && Buffer.byteLength(args.patch) <= 2 * 1024 * 1024, 'patch_limit', 'Patch exceeds the limit.');
                    confirm('apply patch');
                    const argv = ['apply', '--cached', '--whitespace=nowarn', ...(args.reverse ? ['--reverse'] : [])];
                    // git apply refuses traversal and .git paths. Never enable --unsafe-paths.
                    await run([...argv, '--check', '-'], { input: args.patch }); await run([...argv, '-'], { input: args.patch }); break;
                }
                case 'commit': {
                    requireValue(typeof args.message === 'string' && args.message.trim() && args.message.length <= 200000 && !args.message.includes('\0'), 'invalid_message', 'Enter a commit message.');
                    requireValue(typeof args.name === 'string' && args.name.trim() && !/[<>\r\n\0]/.test(args.name) && typeof args.email === 'string' && /^[^<>\s@]+@[^<>\s@]+$/.test(args.email), 'invalid_identity', 'Enter author name and email.');
                    await run(['config', 'user.name', args.name]); await run(['config', 'user.email', args.email]);
                    const argv = ['commit', '--file=-']; if (args.amend) { confirm('amend'); argv.push('--amend'); }
                    await run(argv, { input: args.message }); return { head: (await run(['rev-parse', 'HEAD'])).stdout.toString('utf8').trim() };
                }
                case 'log': return this.#log(run, args);
                case 'show': return { patch: (await run(['show', '--no-ext-diff', '--no-textconv', '--format=fuller', '--stat', '--patch', await this.#revision(repo, args.commit, signal), '--'])).stdout.toString('utf8') };
                case 'branches': return this.#branches(run);
                case 'branch': await run(['branch', '--', branch(), ...(args.start ? [await this.#revision(repo, args.start, signal)] : [])]); break;
                case 'checkout': await run(['switch', '--', branch()]); break;
                case 'deletebranch': confirm(args.branch); await run(['branch', args.force ? '-D' : '-d', '--', branch()]); break;
                case 'tag': branchName(args.name); await run(['tag', '--', args.name, await this.#revision(repo, args.commit ?? 'HEAD', signal)]); break;
                case 'deletetag': branchName(args.name); confirm(args.name); await run(['tag', '-d', '--', args.name]); break;
                case 'remotes': return { text: (await run(['remote', '-v'])).stdout.toString('utf8') };
                case 'addremote': { requireValue(/^[A-Za-z0-9_.-]+$/.test(args.name) && !args.name.startsWith('-'), 'invalid_remote', 'Invalid remote name.'); const url = httpsRemote(args.url, this.#hosts).href; await run(['remote', 'add', '--', args.name, url]); break; }
                case 'fetch': { const name = args.remote ?? 'origin', url = await this.#remote(repo, name, signal); await run(['fetch', '--no-recurse-submodules', ...(args.prune ? ['--prune'] : []), '--', name], { remote: url }); break; }
                case 'pull': { const name = args.remote ?? 'origin', url = await this.#remote(repo, name, signal); await run(['pull', '--ff-only', '--no-recurse-submodules', '--', name, branch()], { remote: url }); break; }
                case 'push': {
                    const name = args.remote ?? 'origin', url = await this.#remote(repo, name, signal), ref = branch(); confirm(args.lease ? 'force-with-lease' : 'push');
                    const argv = ['push', '--porcelain', ...(args.upstream ? ['--set-upstream'] : [])];
                    if (args.lease) { requireValue(/^[a-f0-9]{40}$/.test(args.lease), 'invalid_lease', 'Force-with-lease requires the expected remote SHA.'); confirm('force-with-lease'); argv.push(`--force-with-lease=refs/heads/${ref}:${args.lease}`); }
                    argv.push('--', name, `refs/heads/${ref}:refs/heads/${ref}`); return { text: (await run(argv, { remote: url })).stdout.toString('utf8') };
                }
                case 'merge': await run(['merge', '--no-edit', '--', await this.#revision(repo, args.commit, signal)]); break;
                case 'rebase': confirm('rebase'); await run(['rebase', '--', await this.#revision(repo, args.commit, signal)]); break;
                case 'cherrypick': await run(['cherry-pick', '--', await this.#revision(repo, args.commit, signal)]); break;
                case 'revert': await run(['revert', '--no-edit', '--', await this.#revision(repo, args.commit, signal)]); break;
                case 'continue': case 'abort': case 'skip': {
                    requireValue(['merge', 'rebase', 'cherry-pick', 'revert'].includes(args.operation), 'invalid_operation', 'Unknown sequencer operation.'); requireValue(operation !== 'skip' || args.operation !== 'merge', 'invalid_operation', 'Merge cannot be skipped.'); if (operation !== 'continue') confirm(operation); await run([args.operation, `--${operation}`]); break;
                }
                case 'stash': await run(['stash', 'push', '--include-untracked', '--message', String(args.message ?? 'Work in progress').slice(0, 1000)]); break;
                case 'stashes': return { text: (await run(['stash', 'list', '--format=%gd %H %s'])).stdout.toString('utf8') };
                case 'applystash': case 'popstash': case 'dropstash': {
                    requireValue(Number.isInteger(args.index) && args.index >= 0 && args.index <= 10000, 'invalid_stash', 'Invalid stash index.'); if (operation === 'dropstash') confirm('drop stash');
                    await run(['stash', operation === 'applystash' ? 'apply' : operation === 'popstash' ? 'pop' : 'drop', ...(operation === 'dropstash' ? [] : ['--index']), `stash@{${args.index}}`]); break;
                }
                case 'blame': return { text: (await run(['blame', '--line-porcelain', '--', repositoryPath(args.path)])).stdout.toString('utf8') };
                default: throw new GitError('unsupported_operation', 'Unknown typed Git operation.');
            }
            return { ok: true };
        });
    }
}
