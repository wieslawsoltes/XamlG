import { GitError, SerialQueue, requireValue, text, changes, utf8, base64 } from './core.mjs';
import { WorkspaceStore } from './storage.mjs';
import { GitHubClient, CompanionClient } from './github.mjs';
import { VirtualRepository } from './virtual.mjs';
import { NativeRepository } from './native-client.mjs';
import { layoutCommitGraph } from './graph.mjs';
import { unifiedDiff } from './diff.mjs';
import { editBuffer, saveFileBuffer, loadFileBuffer, captureFileBuffer } from './buffers.mjs';

const sessions = new Map();
const node = (tag, attributes = {}, ...children) => {
    const element = document.createElement(tag);
    for (const [key, value] of Object.entries(attributes)) { if (key.startsWith('on')) element.addEventListener(key.slice(2), value); else if (key === 'class') element.className = value; else if (key in element) element[key] = value; else element.setAttribute(key, value); }
    for (const child of children.flat()) if (child !== null && child !== undefined) element.append(child instanceof Node ? child : document.createTextNode(String(child)));
    return element;
};
const heading = label => node('h3', {}, label);
const info = label => node('p', { class: 'git-note' }, label);
const confirmAction = message => { requireValue(window.confirm(message), 'canceled', 'Operation canceled.'); };
const short = id => id?.slice(0, 9) ?? 'unborn';
const language = path => /\.(axaml|xaml|xml|csproj|props|targets|slnx)$/i.test(path) ? 'xml' : /\.cs$/i.test(path) ? 'csharp' : /\.json$/i.test(path) ? 'json' : /\.(js|mjs)$/i.test(path) ? 'javascript' : /\.md$/i.test(path) ? 'markdown' : 'plaintext';

class Session extends EventTarget {
    store = new WorkspaceStore(); api = new GitHubClient(); repositories = new Map(); documents = new Map(); current = null; companion = null; busy = false; error = ''; queue = new SerialQueue(); author = { name: '', email: '' }; oauth = null; retired = false;
    constructor(id) {
        super(); this.id = id;
        this.ready = this.store.list().then(states => { for (const state of states) this.add(new VirtualRepository(this.store, this.api, state)); if (!this.current) this.current = this.repositories.values().next().value ?? null; this.changed(); }).catch(error => { this.error = error.message; this.changed(); });
    }
    changed() { this.dispatchEvent(new Event('change')); }
    add(repository) { this.repositories.set(repository.state.id, repository); repository.addEventListener('change', () => this.changed()); this.current = repository; this.changed(); return repository; }
    async perform(action) { return this.queue.run(async () => { requireValue(!this.retired, 'retired_session', 'The Git workbench has been disposed.'); this.busy = true; this.error = ''; this.changed(); try { return await action(); } catch (error) { if (error.code !== 'canceled') this.error = error.message; throw error; } finally { this.busy = false; this.changed(); } }); }
    async open(dotnet, repository, type, options = {}) {
        const key = JSON.stringify([repository.state.id, type, options.path, options.staged, options.commit, options.title, type === 'output' ? crypto.randomUUID() : null]);
        let document = [...this.documents.values()].find(d => d.key === key);
        if (!document) { const id = 'gitdoc:' + crypto.randomUUID(); document = { id, key, repository, type, path: options.path, staged: options.staged ?? false, commit: options.commit, title: options.title ?? (type === 'file' ? options.path : type === 'commit' ? `Commit ${short(options.commit)}` : `${options.staged ? 'Staged' : 'Diff'} · ${options.path}`), text: options.text, loaded: options.text !== undefined, dirty: false }; this.documents.set(id, document); }
        if (type === 'output' && options.text !== undefined) { document.text = options.text; document.loaded = true; }
        await dotnet.invokeMethodAsync('OpenGitDocument', { id: document.id, title: document.title }); return document;
    }
}
function getSession(id) { if (!sessions.has(id)) sessions.set(id, new Session(id)); return sessions.get(id); }
if (typeof window !== 'undefined') window.addEventListener('beforeunload', event => { if ([...sessions.values()].some(s => [...s.documents.values()].some(d => d.dirty))) { event.preventDefault(); event.returnValue = ''; } });

class ToolView {
    drafts = {}; disposed = false;
    constructor(host, session, kind, dotnet) { this.host = host; this.session = session; this.kind = kind.replace(/^git-/, ''); this.dotnet = dotnet; this.listener = () => this.render(); session.addEventListener('change', this.listener); this.render(); }
    field(name, label, value = '', type = 'text') {
        const input = node(type === 'textarea' ? 'textarea' : 'input', { ...(type !== 'textarea' ? { type } : {}), 'aria-label': label, placeholder: label, value: this.drafts[name] ?? value, autocomplete: type === 'password' ? 'off' : 'on', oninput: event => { if (type !== 'password') this.drafts[name] = event.target.value; } });
        return { input, element: node('label', { class: 'git-field' }, node('span', {}, label), input), value: () => input.value };
    }
    button(label, action, attributes = {}) { return node('button', { type: 'button', disabled: this.session.busy, onclick: () => { void this.session.perform(action).catch(() => {}); }, ...attributes }, label); }
    row(...children) { return node('div', { class: 'git-row' }, ...children); }
    async open(repository, type, options) { return this.session.open(this.dotnet, repository, type, options); }
    render() {
        if (this.disposed) return;
        const s = this.session, root = node('section', { class: 'git-tool', 'aria-label': 'Git ' + this.kind, 'aria-busy': String(s.busy) });
        root.append(node('header', { class: 'git-heading' }, node('strong', {}, 'GIT · ' + this.kind.toUpperCase()), node('span', { role: 'status' }, s.busy ? 'Working…' : s.current?.state.name ?? 'No repository')));
        if (s.error) root.append(node('p', { class: 'git-error', role: 'alert' }, s.error));
        if (this.kind !== 'repositories' && this.kind !== 'github') {
            const select = node('select', { 'aria-label': 'Active Git repository', disabled: s.busy, onchange: e => { s.current = s.repositories.get(e.target.value); s.changed(); } }, [...s.repositories.values()].map(r => node('option', { value: r.state.id, selected: r === s.current }, `${r.native ? 'Native' : 'Virtual'} · ${r.state.name}`)));
            root.append(select);
            if (!s.current) { root.append(info('Open Repositories to create an offline workspace or open an HTTPS remote.')); this.host.replaceChildren(root); return; }
        }
        const method = { repositories: 'repositories', changes: 'changes', branches: 'branches', history: 'history', github: 'github' }[this.kind];
        if (method) this[method](root); else root.append(info('Unknown Git tool.'));
        this.host.replaceChildren(root);
    }
    repositories(root) {
        const s = this.session, name = this.field('name', 'Workspace name', 'Repository'), url = this.field('url', 'HTTPS repository URL', 'https://github.com/owner/repo'), branch = this.field('branch', 'Branch (blank uses default)');
        root.append(name.element, url.element, branch.element, this.row(
            this.button('New virtual', async () => s.add(await VirtualRepository.create(s.store, s.api, { name: name.value(), branch: branch.value() || 'main' }))),
            this.button('Open GitHub', async () => s.add(await VirtualRepository.clone(s.store, s.api, url.value(), branch.value() || undefined))),
            this.button('Clone HTTPS', async () => { requireValue(s.companion, 'companion_required', 'Connect the native companion in GitHub first.'); const item = await s.companion.call('git/clone', { url: url.value(), branch: branch.value() || undefined }); const r = new NativeRepository(s.companion, { ...item, name: name.value() }); await r.refresh(); s.add(r); }),
            this.button('New native', async () => { requireValue(s.companion, 'companion_required', 'Connect the native companion first.'); const item = await s.companion.call('git/init', { name: name.value(), branch: branch.value() || 'main' }); const r = new NativeRepository(s.companion, item); await r.refresh(); s.add(r); })
        ), info('Virtual repositories live in this browser. Native Git uses your private companion. Opening a repository never runs its code.'));
        root.append(heading('Open repositories'));
        for (const repo of s.repositories.values()) root.append(this.button(`${repo === s.current ? '● ' : ''}${repo.state.name} · ${repo.native ? 'native' : 'virtual'} · ${repo.state.branch}`, () => { s.current = repo; s.changed(); }, { class: 'git-wide' }));
        const repo = s.current; if (!repo) return;
        root.append(heading('Working files'));
        const search = this.field('fileFilter', 'Filter repository paths'), list = node('div', { class: 'git-files' });
        const renderFiles = () => {
            const paths = [...repo.state.worktree.keys()].sort().filter(p => p.toLowerCase().includes(search.value().toLowerCase()));
            list.replaceChildren(...paths.slice(0, 500).map(path => this.button(path, () => this.open(repo, 'file', { path }), { class: 'git-file', title: path })));
            if (paths.length > 500) list.append(info(`${paths.length} matches; showing 500. Refine the filter to reach any file.`));
        };
        search.input.addEventListener('input', renderFiles); root.append(search.element, list); renderFiles();
        const path = this.field('newPath', 'New file path'); root.append(path.element, this.row(this.button('Create file', async () => { await repo.write(path.value(), '', { expectedOid: null }); await this.open(repo, 'file', { path: path.value() }); }), this.button('Refresh', () => repo.refresh())));
        if (!repo.native && !repo.state.remote) root.append(this.button('Set GitHub remote', () => repo.setRemote(url.value())));
    }
    changes(root) {
        const s = this.session, repo = s.current, status = repo.status();
        root.append(this.row(this.button('Refresh', () => repo.refresh()), this.button('Fetch', () => repo.fetch()), this.button('Pull FF', () => repo.pull()), this.button('Push', async () => { confirmAction(`Push committed changes on ${repo.state.branch}?`); await repo.push(); }), this.button('Publish branch', async () => { confirmAction(`Create remote branch ${repo.state.branch}?`); await repo.push({ create: true }); })));
        if (status.pendingPush) root.append(info('Push confirmation is pending. Local edits are protected until reconciliation.'), this.button('Reconcile push', () => repo.reconcilePush()));
        if (status.conflicts.length) {
            root.append(heading('Conflicts'));
            for (const path of status.conflicts) root.append(this.row(node('span', {}, path), this.button('Open resolution', () => this.open(repo, 'file', { path })), ...(repo.native ? [this.button('Stage resolution', () => repo.stage([path]))] : [this.button('Use ours', () => repo.resolve(path, 'ours')), this.button('Use theirs', () => repo.resolve(path, 'theirs')), this.button('Mark working resolved', () => repo.resolve(path, 'worktree'))])));
        }
        for (const [label, entries, staged] of [['Staged', status.staged, true], ['Working changes', status.unstaged, false]]) {
            root.append(heading(`${label} (${entries.length})`));
            if (entries.length) root.append(this.button(staged ? 'Unstage all' : 'Stage all', () => repo[staged ? 'unstage' : 'stage'](entries.map(e => e.path))));
            for (const entry of entries) root.append(this.row(node('span', { class: 'git-kind' }, entry.kind), this.button(entry.path, () => this.open(repo, 'diff', { path: entry.path, staged }), { class: 'git-path' }), this.button(staged ? 'Unstage' : 'Stage', () => repo[staged ? 'unstage' : 'stage']([entry.path])), ...(!staged ? [this.button('Edit', () => this.open(repo, 'file', { path: entry.path })), this.button('Discard', async () => { confirmAction(`Discard unstaged changes to ${entry.path}? This cannot be undone.`); await repo.discard([entry.path], 'discard'); })] : [])));
        }
        const name = this.field('authorName', 'Author name', s.author.name || s.api.user?.name || s.api.user?.login || ''), email = this.field('authorEmail', 'Author email', s.author.email || s.api.user?.email || '', 'email'), message = this.field('commitMessage', 'Commit message', '', 'textarea');
        root.append(heading('Commit'), name.element, email.element, message.element, this.button('Commit staged', async () => { s.author = { name: name.value(), email: email.value() }; await repo.commit(message.value(), s.author); this.drafts.commitMessage = ''; }));
        if (repo.native && status.operation?.length) {
            const operation = status.operation.includes('MERGE_HEAD') ? 'merge' : status.operation.some(x => x.startsWith('rebase')) ? 'rebase' : status.operation.includes('CHERRY_PICK_HEAD') ? 'cherry-pick' : 'revert';
            root.append(info(`Operation in progress: ${operation}`), this.row(this.button('Continue operation', () => repo.action('continue', { operation })), this.button('Abort operation', async () => { confirmAction(`Abort ${operation} and discard its resolutions?`); await repo.action('abort', { operation, confirm: 'abort' }); })));
        } else if (!repo.native && repo.state.merge) root.append(this.button('Abort merge', async () => { confirmAction('Discard merge resolutions and restore the original working tree?'); await repo.abortMerge('abort'); }));
    }
    branches(root) {
        const repo = this.session.current, state = repo.state, name = this.field('newBranch', 'New branch name'); root.append(name.element, this.button('Create branch', () => repo.createBranch(name.value())), heading('Local branches'));
        for (const [branch, id] of state.refs) root.append(this.row(node('span', { class: 'git-path' }, `${branch === state.branch ? '● ' : ''}${branch}`), node('code', {}, short(id)), this.button('Checkout', () => repo.checkout(branch)), this.button('Merge', () => repo.merge(id)), this.button('Delete', async () => { confirmAction(`Delete local branch ${branch}?`); await repo.deleteBranch(branch, branch); })));
        root.append(heading('Stashes')); const message = this.field('stashMessage', 'Stash message', 'Work in progress'); root.append(message.element, this.button('Stash working changes', () => repo.stash(message.value())));
        if (repo.native) {
            root.append(node('pre', { class: 'git-output' }, repo.stashes || 'No stashes')); const index = this.field('stashIndex', 'Stash index', '0', 'number'); root.append(index.element, this.row(this.button('Apply stash', () => repo.applyStash(Number(index.value()))), this.button('Pop stash', () => repo.applyStash(Number(index.value()), true))));
            const operation = node('select', { 'aria-label': 'Native Git operation' }, ['rebase', 'cherrypick', 'revert', 'tag', 'deletetag', 'addremote', 'deletebranch', 'patch', 'blame', 'push', 'skip', 'abort'].map(value => node('option', { value }, value))), args = this.field('nativeArgs', 'Typed operation arguments (JSON)', '{"commit":"HEAD"}', 'textarea');
            root.append(heading('Advanced native Git'), info('Typed operations only; no shell command execution. Destructive operations also require their explicit confirm argument.'), operation, args.element, this.button('Run operation', async () => { confirmAction(`Run native Git ${operation.value}?`); const result = await repo.action(operation.value, JSON.parse(args.value())); await this.open(repo, 'output', { title: `Git ${operation.value}`, text: JSON.stringify(result, null, 2) }); }));
        } else for (const stash of state.stashes) root.append(this.row(node('span', { class: 'git-path' }, stash.message), this.button('Apply', () => repo.applyStash(stash.id)), this.button('Pop', () => repo.applyStash(stash.id, true)), this.button('Drop', async () => { confirmAction(`Permanently drop stash ${stash.message}?`); await repo.dropStash(stash.id, stash.id); })));
    }
    history(root) {
        const repo = this.session.current; root.append(this.row(this.button('Refresh', () => repo.refresh()), ...(!repo.native ? [this.button('Load more history', () => repo.loadHistory(100))] : [])));
        const commits = repo.history(); if (!commits.length) root.append(info('No commits yet.'));
        const list = node('div', { class: 'git-history' });
        const graph = layoutCommitGraph(commits);
        for (const row of graph.rows) {
            const commit = row.commit, svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            svg.setAttribute('width', String(graph.width * 18 + 12)); svg.setAttribute('height', '28'); svg.setAttribute('aria-hidden', 'true');
            const shape = (kind, attributes) => { const element = document.createElementNS(svg.namespaceURI, kind); for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, String(value)); svg.append(element); };
            const x = lane => 12 + lane * 18;
            for (const edge of row.edges) shape('path', { d: `M${x(edge.from)},${edge.type === 'through' ? 0 : 14} L${x(edge.to)},28`, fill: 'none', stroke: 'currentColor', 'stroke-width': 1.5 });
            if (row.incoming) shape('path', { d: `M${x(row.lane)},0 V14`, stroke: 'currentColor', 'stroke-width': 1.5 });
            shape('circle', { cx: x(row.lane), cy: 14, r: commit.parents?.length > 1 ? 4.5 : 3.5, fill: row.unloaded ? 'none' : 'currentColor', stroke: 'currentColor' });
            const label = `${short(commit.oid)} ${commit.unloaded ? 'Load history to inspect this commit' : commit.message?.split('\n')[0] ?? ''}`;
            const button = this.button(label, () => this.open(repo, 'commit', { commit: commit.oid }), { class: 'git-history-row', disabled: this.session.busy || !!commit.unloaded });
            button.prepend(svg); list.append(button);
        }
        root.append(list);
        if (graph.boundaries.length) root.append(info(`${graph.boundaries.length} parent connection(s) continue beyond the loaded history.`));
    }
    github(root) {
        const s = this.session, token = this.field('pat', 'GitHub personal access token', '', 'password');
        root.append(info(s.api.user ? `Signed in as ${s.api.user.login}` : 'Public repositories work without sign-in. Private repositories and writes require authorization.'), token.element, this.row(this.button('Sign in with token', async () => { const value = token.value(); token.input.value = ''; if (s.companion) { const result = await s.companion.call('github/login', { token: value }); s.api.useBroker(s.companion); s.api.user = result.user; } else await s.api.signIn(value); }), this.button('Sign out', async () => { s.oauth?.abort(); s.api.signOut(); delete this.drafts.device; delete this.drafts.authorizeUrl; delete this.drafts.oauthHandle; if (s.companion) await s.companion.call('github/logout'); })));
        const address = this.field('companionAddress', 'Companion origin', 'http://127.0.0.1:47831'), owner = this.field('ownerCredential', 'Companion owner credential', '', 'password');
        root.append(heading('Native Git and OAuth'), address.element, owner.element, this.button('Connect companion', async () => {
            const client = new CompanionClient(address.value(), owner.value()); owner.input.value = ''; await client.call('health'); s.companion = client;
            for (const item of await client.call('git/list')) { if (s.repositories.has(item.id)) continue; const repo = new NativeRepository(client, item); await repo.refresh(); s.add(repo); }
        }), this.button('Start GitHub OAuth', async () => {
            requireValue(s.companion, 'companion_required', 'Connect the private companion first.');
            delete this.drafts.device; const result = await s.companion.call('oauth/start'), url = new URL(result.authorizeUrl); requireValue(url.origin === 'https://github.com' && url.pathname === '/login/oauth/authorize', 'invalid_oauth_url', 'Unexpected OAuth authorization URL.');
            this.drafts.authorizeUrl = url.href; this.drafts.oauthHandle = result.handle; this.drafts.oauthExpires = result.expiresAt;
        }));
        if (this.drafts.authorizeUrl) root.append(node('a', { href: this.drafts.authorizeUrl, target: '_blank', rel: 'noopener noreferrer', class: 'git-authorize' }, 'Authorize XamlG on GitHub'), this.button('Complete OAuth sign-in', async () => { const result = await s.companion.call('oauth/status', { handle: this.drafts.oauthHandle }); requireValue(result.status === 'authorized', 'oauth_pending', result.status === 'failed' ? 'Authorization failed. Start again.' : 'Complete authorization in the GitHub tab first.'); s.api.useBroker(s.companion); s.api.user = result.user; delete this.drafts.authorizeUrl; delete this.drafts.oauthHandle; }));
        root.append(info('Tokens stay in memory. OAuth client secrets belong only in the companion environment. Never enter secrets into repository files.'), heading('GitHub REST / GraphQL'));
        root.append(this.button('Start device sign-in', async () => {
            requireValue(s.companion, 'companion_required', 'Connect the companion and configure an OAuth application with device flow enabled.');
            delete this.drafts.authorizeUrl; delete this.drafts.oauthHandle;
            this.drafts.device = await s.companion.call('oauth/device/start');
        }));
        if (this.drafts.device) {
            const device = this.drafts.device;
            root.append(info('Enter this one-time user code on GitHub:'), node('code', { class: 'git-device-code' }, device.userCode),
                node('a', { href: 'https://github.com/login/device', target: '_blank', rel: 'noopener noreferrer', class: 'git-authorize' }, 'Open GitHub device authorization'),
                info(device.status === 'pending' ? `Pending authorization. GitHub requires at least ${device.retryAfter} seconds before the next check.` : `Device sign-in: ${device.status}`),
                this.row(this.button('Check device sign-in', async () => {
                    const result = await s.companion.call('oauth/device/poll', { handle: device.handle }); this.drafts.device = result;
                    if (result.status === 'authorized') { s.api.useBroker(s.companion); s.api.user = result.user; delete this.drafts.device; }
                }), this.button('Cancel device sign-in', async () => { await s.companion.call('oauth/device/cancel', { handle: device.handle }); delete this.drafts.device; })));
        }
        if (this.drafts.oauthHandle) root.append(this.button('Cancel OAuth sign-in', async () => {
            await s.companion.call('oauth/cancel', { handle: this.drafts.oauthHandle }); delete this.drafts.oauthHandle; delete this.drafts.authorizeUrl;
        }));
        const method = node('select', { 'aria-label': 'GitHub API method', value: this.drafts.apiMethod ?? 'GET', onchange: e => { this.drafts.apiMethod = e.target.value; } }, ['GET', 'POST', 'PATCH', 'PUT', 'DELETE', 'HEAD'].map(value => node('option', { value, selected: value === (this.drafts.apiMethod ?? 'GET') }, value)));
        const path = this.field('apiPath', 'GitHub API path', '/user/repos?per_page=30'), body = this.field('apiBody', 'JSON request body', '', 'textarea');
        root.append(method, path.element, body.element, this.button('Send API request', async () => { if (!['GET', 'HEAD'].includes(method.value)) confirmAction(`Send ${method.value} ${path.value()}? This may modify your GitHub account or repositories.`); const result = await s.api.response(method.value, path.value(), body.value().trim() ? JSON.parse(body.value()) : undefined); const repo = s.current ?? { state: { id: 'github', name: 'GitHub' } }; await this.open(repo, 'output', { title: `${method.value} ${path.value()}`, text: JSON.stringify(result, null, 2) }); }));
        if (s.current?.state.remote && !s.current.native) {
            const base = s.current.apiPath; root.append(heading('Repository shortcuts'));
            for (const [label, suffix] of [['Pull requests', '/pulls?state=open&per_page=100'], ['Issues', '/issues?state=open&per_page=100'], ['Actions', '/actions/runs?per_page=50'], ['Releases', '/releases?per_page=50'], ['Branches', '/branches?per_page=100']]) root.append(this.button(label, async () => { const result = await s.api.request('GET', base + suffix); await this.open(s.current, 'output', { title: label, text: JSON.stringify(result, null, 2) }); }));
        }
    }
    dispose() { this.disposed = true; this.session.removeEventListener('change', this.listener); this.host.replaceChildren(); }
}

async function loadDocument(document) {
    const repo = document.repository;
    if (document.type === 'file') {
        await loadFileBuffer(document);
    } else if (document.type === 'diff') document.diff = await repo.diff(document.path, document.staged);
    else if (document.type === 'commit') {
        if (repo.native) document.text = (await repo.raw('show', { commit: document.commit })).patch;
        else { const commit = repo.state.commits.get(document.commit); requireValue(commit, 'history_required', 'Load this commit first.'); document.text = `${commit.oid}\n${commit.author.name} <${commit.author.email}>\n${commit.author.date}\nParents: ${commit.parents.join(', ')}\n\n${commit.message}`; const parent = repo.state.commits.get(commit.parents[0]); document.commitFiles = parent || !commit.parents.length ? changes(parent?.files ?? new Map(), commit.files) : null; }
    } else if (document.type === 'commit-file') {
        const commit = repo.state.commits.get(document.commit), before = commit.parents[0] ? await repo.readAt(commit.parents[0], document.path) : new Uint8Array(), after = await repo.readAt(document.commit, document.path);
        try { document.diff = { ...unifiedDiff(document.path, text(before), text(after)), before: text(before), after: text(after) }; } catch (error) { if (error.code !== 'binary_file') throw error; document.diff = { patch: 'Binary file changed.', hunks: [], binary: true }; }
    }
    document.loaded = true;
}
export function mountTool(host, sessionId, kind, dotnet) { const view = new ToolView(host, getSession(sessionId), kind, dotnet); return { dispose: () => view.dispose() }; }
export async function mountDocument(host, sessionId, documentId, dotnet) {
    const session = getSession(sessionId), state = session.documents.get(documentId); requireValue(state, 'missing_document', 'This Git document is no longer available. Reopen it from the Git tools.');
    let disposed = false, renderRevision = 0, cleanups = []; const cleanup = () => { for (const action of cleanups.splice(0)) action(); };
    const perform = action => { void session.perform(action).catch(error => { const errorNode = host.querySelector('[data-git-error]'); if (errorNode) { errorNode.textContent = error.message; errorNode.hidden = false; } }); };
    const button = (label, action) => node('button', { type: 'button', onclick: () => perform(action) }, label);
    async function render() {
        const revision = ++renderRevision;
        cleanup(); if (disposed) return;
        const root = node('section', { class: 'git-document', 'data-git-document': documentId }), toolbar = node('header', { class: 'git-document-toolbar' }, node('strong', { class: 'git-path' }, state.title)), error = node('p', { class: 'git-error', role: 'alert', 'data-git-error': '', hidden: true });
        const status = node('span', { role: 'status' }, state.dirty ? 'Unsaved buffer' : ''); toolbar.append(status, button('Reload', async () => { if (state.dirty) confirmAction('Discard unsaved editor changes and reload from the repository?'); await loadDocument(state); await render(); })); root.append(toolbar, error); host.replaceChildren(root);
        if (!state.loaded) { try { await loadDocument(state); } catch (failure) { error.textContent = failure.message; error.hidden = false; return; } }
        if (disposed || revision !== renderRevision) return;
        if (state.type === 'file') {
            if (!state.binary) toolbar.append(button('Save', async () => { await saveFileBuffer(state); status.textContent = state.dirty ? 'Saved snapshot · newer edits remain unsaved' : 'Saved to working tree'; }), button('Stage saved file', async () => { requireValue(!state.dirty, 'unsaved_buffer', 'Save this editor before staging.'); await state.repository.stage([state.path]); status.textContent = 'Staged'; }), button('Diff', () => session.open(dotnet, state.repository, 'diff', { path: state.path })));
            if (!state.binary && /\.(cs|axaml|xaml)$/i.test(state.path)) {
                toolbar.append(button('Copy into Studio', async () => {
                    const defaultPath = state.studioPath ?? (/\.cs$/i.test(state.path) ? 'Code.cs' : 'View.axaml'), targetPath = window.prompt('Studio target path. Existing source will be replaced; this import is undoable. Automatic compile/preview will be disabled.', defaultPath);
                    requireValue(targetPath, 'canceled', 'Import canceled.'); await dotnet.invokeMethodAsync('ImportGitSource', { path: targetPath, text: state.text }); state.studioPath = targetPath; status.textContent = 'Copied to Studio without execution';
                }), button('Capture Studio edits', async () => { const path = window.prompt('Studio source path to copy back into this editor:', state.studioPath ?? state.path); requireValue(path, 'canceled', 'Capture canceled.'); if (state.dirty) confirmAction('Replace this unsaved Git buffer with the Studio source?'); await captureFileBuffer(state, () => dotnet.invokeMethodAsync('ReadStudioSource', path)); state.studioPath = path; await render(); }));
            }
            const editorHost = node('div', { class: 'git-editor' }); root.append(editorHost);
            if (state.binary) editorHost.append(node('pre', { class: 'git-output' }, state.text));
            else {
                // Reuse Studio's exact source buffer mapping, including mixed EOLs and BOMs.
                const { SourceBuffer } = await import('../source-buffer.js'); if (disposed || revision !== renderRevision) return;
                const source = new SourceBuffer(state.text, !!globalThis.monaco?.editor), changed = value => { editBuffer(state, value); status.textContent = state.dirty ? 'Unsaved buffer' : ''; };
                if (globalThis.monaco?.editor) {
                    const monaco = globalThis.monaco, model = monaco.editor.createModel(state.text, language(state.path)), editor = monaco.editor.create(editorHost, { model, automaticLayout: true, minimap: { enabled: false }, fontSize: 13, theme: document.documentElement.dataset.theme === 'light' ? 'vs' : 'vs-dark' });
                    const subscription = model.onDidChangeContent(event => { if (event.isFlush) source.set(model.getValue(undefined, true)); else if (event.isEolChange) source.changeEol(event.eol); else source.applyChanges(event.changes); changed(source.text); });
                    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => perform(async () => { await saveFileBuffer(state); status.textContent = state.dirty ? 'Saved snapshot · newer edits remain unsaved' : 'Saved to working tree'; })); cleanups.push(() => { subscription.dispose(); editor.dispose(); model.dispose(); });
                } else {
                    const input = node('textarea', { 'aria-label': state.path + ' Git editor', class: 'git-editor-fallback', value: state.text, spellcheck: false }); let displayed = input.value;
                    input.addEventListener('input', () => { source.replaceDisplayed(displayed, input.value); displayed = input.value; changed(source.text); }); editorHost.append(input);
                }
            }
        } else if (state.diff) {
            const diff = state.diff;
            if (state.type === 'diff') toolbar.append(button(state.staged ? 'Unstage file' : 'Stage file', () => state.repository[state.staged ? 'unstage' : 'stage']([state.path])));
            if (diff.bounded) root.append(info('The diff work limit was reached. This exact replacement diff is valid but may not be minimal.'));
            if (!diff.binary && typeof state.repository.stageHunks === 'function' && !state.staged && state.type === 'diff') {
                const hunkBar = node('div', { class: 'git-row' }), selected = new Set();
                for (const hunk of diff.hunks) hunkBar.append(node('label', {}, node('input', { type: 'checkbox', 'aria-label': `Stage hunk ${hunk.id + 1}`, onchange: e => e.target.checked ? selected.add(hunk.id) : selected.delete(hunk.id) }), `Hunk ${hunk.id + 1}`));
                hunkBar.append(button('Stage selected hunks', async () => { await state.repository.stageHunks(state.path, [...selected], diff.token); await loadDocument(state); await render(); })); root.append(hunkBar);
            }
            if (globalThis.monaco?.editor && diff.before !== undefined && !diff.binary) {
                const box = node('div', { class: 'git-editor' }); root.append(box); const monaco = globalThis.monaco, original = monaco.editor.createModel(diff.before, language(state.path)), modified = monaco.editor.createModel(diff.after, language(state.path)), editor = monaco.editor.createDiffEditor(box, { readOnly: true, automaticLayout: true, renderSideBySide: true, minimap: { enabled: false } }); editor.setModel({ original, modified }); cleanups.push(() => { editor.dispose(); original.dispose(); modified.dispose(); });
            } else root.append(node('pre', { class: 'git-output git-diff', tabindex: 0 }, diff.patch));
        } else {
            root.append(node('pre', { class: 'git-output', tabindex: 0 }, state.text ?? ''));
            if (state.type === 'commit' && !state.repository.native) {
                if (!state.commitFiles) root.append(info('Load parent history to inspect the exact commit diff.'));
                else for (const file of state.commitFiles) root.append(button(`${file.kind} · ${file.path}`, () => session.open(dotnet, state.repository, 'commit-file', { path: file.path, commit: state.commit, title: `${short(state.commit)} · ${file.path}` })));
            }
        }
    }
    await render(); return { dispose: () => { disposed = true; renderRevision++; cleanup(); host.replaceChildren(); } };
}
export async function disposeSession(id) { const session = sessions.get(id); if (!session) return; requireValue(![...session.documents.values()].some(d => d.dirty), 'unsaved_buffers', 'Save dirty Git documents before disposing the workspace.'); session.companion?.dispose(); session.api.signOut(); await session.store.close(); sessions.delete(id); }

// The cached ES module is shared, but each Razor component owns this fresh facade.
// Disposing one component's JS reference cannot invalidate another mounted tool.
export function createWorkbenchInterop() { return { mountTool, mountDocument, retireSession }; }
export async function retireSession(id) {
    const session = sessions.get(id); if (!session) return;
    session.retired = true; session.api.signOut(); session.companion?.dispose();
    return session.queue.run(async () => {
        await session.ready; await session.store.close();
        const dirty = [...session.documents.values()].filter(document => document.dirty).length;
        // Never silently erase unsaved buffers when a host retires. They remain
        // memory-only until this page is unloaded; persisted repository data stays intact.
        if (!dirty) sessions.delete(id);
        return { dirtyBuffers: dirty };
    });
}
