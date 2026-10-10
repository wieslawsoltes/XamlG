import { createHash } from 'node:crypto';
import { GitError, buildTrees, makeCommit, gitHash, utf8, base64, unbase64 } from '../../tools/XamlG.Playground/wwwroot/git/core.mjs';
export class FakeGitHub {
    refs = new Map(); commits = new Map(); trees = new Map(); blobs = new Map(); calls = []; forceTruncated = false; failRef = null;
    async seed(files, { branch = 'main', parents = [], message = 'seed' } = {}) {
        const snapshot = new Map();
        for (const [path, value] of Object.entries(files)) { const bytes = typeof value === 'string' ? utf8(value) : value, id = await gitHash('blob', bytes); this.blobs.set(id, bytes); snapshot.set(path, { oid: id, mode: '100644' }); }
        const tree = await buildTrees(snapshot); for (const object of tree.objects) this.trees.set(object.oid, object.entries);
        const commit = await makeCommit(tree.oid, parents, message, { name: 'Test', email: 'test@example.com', date: '2026-01-01T00:00:00Z' }); this.commits.set(commit.oid, commit); this.refs.set(branch, commit.oid); return commit.oid;
    }
    ancestors(start) { const seen = new Set(), q = [start]; for (let i = 0; i < q.length; i++) if (!seen.has(q[i])) { seen.add(q[i]); q.push(...(this.commits.get(q[i])?.parents ?? [])); } return seen; }
    async request(method, path, body) {
        this.calls.push({ method, path, body });
        const url = new URL(path, 'https://api.github.com'), p = decodeURIComponent(url.pathname).replace(/^\/repos\/owner\/repo/, '');
        if (method === 'GET' && p === '') return { default_branch: 'main', size: this.refs.size ? 1 : 0 };
        if (p.startsWith('/git/ref/heads/') && method === 'GET') { const id = this.refs.get(p.slice(15)); if (!id) throw new GitError(this.refs.size ? 'github_404' : 'github_409', 'Not found or empty repository.'); return { object: { sha: id } }; }
        if (p.startsWith('/git/commits/') && method === 'GET') { const c = this.commits.get(p.slice(13)); if (!c) throw new GitError('github_404', 'Commit not found.'); return { ...c, tree: { sha: c.tree }, parents: c.parents.map(sha => ({ sha })) }; }
        if (p.startsWith('/git/trees/') && method === 'GET') {
            const sha = p.slice(11), entries = this.trees.get(sha); if (!entries) throw new GitError('github_404', 'Tree not found.');
            const recursive = url.searchParams.has('recursive'), output = [];
            const visit = (values, prefix) => { for (const e of values) { const entry = { path: prefix + e.name, mode: e.mode === '40000' ? '040000' : e.mode, type: e.mode === '40000' ? 'tree' : e.mode === '160000' ? 'commit' : 'blob', sha: e.oid }; output.push(entry); if (recursive && entry.type === 'tree') visit(this.trees.get(e.oid), entry.path + '/'); } };
            visit(entries, ''); return { sha, tree: this.forceTruncated && recursive ? output.slice(0, 1) : output, truncated: this.forceTruncated && recursive };
        }
        if (p.startsWith('/git/blobs/') && method === 'GET') { const bytes = this.blobs.get(p.slice(11)); if (!bytes) throw new GitError('github_404', 'Blob not found.'); return { encoding: 'base64', content: base64(bytes), size: bytes.length }; }
        if (p === '/git/blobs' && method === 'POST') { const bytes = unbase64(body.content), sha = await gitHash('blob', bytes); this.blobs.set(sha, bytes); return { sha }; }
        if (p === '/git/trees' && method === 'POST') {
            const entries = body.tree.map(e => ({ name: e.path, mode: e.mode === '040000' ? '40000' : e.mode, oid: e.sha }));
            entries.sort((a, b) => Buffer.compare(Buffer.from(a.name + (a.mode === '40000' ? '/' : '')), Buffer.from(b.name + (b.mode === '40000' ? '/' : ''))));
            const bytes = Buffer.concat(entries.map(e => Buffer.concat([Buffer.from(`${e.mode} ${e.name}\0`), Buffer.from(e.oid, 'hex')]))), sha = createHash('sha1').update(`tree ${bytes.length}\0`).update(bytes).digest('hex'); this.trees.set(sha, entries); return { sha };
        }
        if (p === '/git/commits' && method === 'POST') { const c = await makeCommit(body.tree, body.parents, body.message, body.author); this.commits.set(c.oid, c); return { sha: c.oid }; }
        if (method === 'PATCH' && p.startsWith('/git/refs/heads/') || method === 'POST' && p === '/git/refs') {
            if (method === 'POST' && !this.refs.size) throw new GitError('github_422', 'Cannot create refs in an empty repository.');
            const branch = method === 'PATCH' ? p.slice(16) : body.ref.slice(11), current = this.refs.get(branch);
            if (this.failRef === 'before') { this.failRef = null; throw new Error('connection lost'); }
            if (method === 'POST' && current || current && !this.ancestors(body.sha).has(current)) throw new GitError('github_422', 'Non-fast-forward.');
            if (method === 'PATCH' && body.force !== false) throw new Error('unsafe force');
            this.refs.set(branch, body.sha); if (this.failRef === 'after') { this.failRef = null; throw new Error('response lost'); } return { object: { sha: body.sha } };
        }
        if (p.startsWith('/compare/')) { const [a, b] = p.slice(9).split('...'); return { status: a === b ? 'identical' : this.ancestors(b).has(a) ? 'ahead' : this.ancestors(a).has(b) ? 'behind' : 'diverged' }; }
        throw new Error(`Unhandled ${method} ${p}`);
    }
}
