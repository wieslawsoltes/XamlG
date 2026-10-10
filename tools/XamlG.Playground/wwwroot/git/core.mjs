/** Shared, dependency-free Git primitives. No repository content is executable. */
export const LIMITS = Object.freeze({ responseBytes: 32 * 1024 * 1024, blobBytes: 16 * 1024 * 1024, files: 100000, diffWork: 2000000 });
export class GitError extends Error {
    constructor(code, message, details) { super(message); this.name = 'GitError'; this.code = code; this.details = details; }
}
export function requireValue(condition, code, message) { if (!condition) throw new GitError(code, message); }
export class SerialQueue {
    #tail = Promise.resolve();
    run(action) { const result = this.#tail.then(action); this.#tail = result.catch(() => {}); return result; }
}
export function repositoryPath(path) {
    requireValue(typeof path === 'string' && path.length > 0 && path.length <= 4096 && !/[\\\0-\x1f\x7f:]/.test(path), 'invalid_path', 'Invalid repository-relative path.');
    requireValue(path.split('/').every(p => p && p !== '.' && p !== '..' && !/^\.git$/i.test(p) && !/[. ]$/.test(p)), 'invalid_path', 'Paths must stay inside the repository and outside .git.');
    return path;
}
export function branchName(name) {
    requireValue(typeof name === 'string' && name.length > 0 && name.length < 1024 && name !== '@' && !name.startsWith('-') && !/[\x00-\x20\x7f~^:?*\[\\]/.test(name) && !name.includes('..') && !name.includes('@{') && !name.endsWith('.'), 'invalid_ref', 'Invalid Git branch or tag name.');
    requireValue(name.split('/').every(p => p && !p.startsWith('.') && !p.endsWith('.lock')), 'invalid_ref', 'Invalid Git branch or tag name.');
    return name;
}
export function oid(value) { requireValue(typeof value === 'string' && /^[a-f0-9]{40}$/.test(value), 'invalid_oid', 'Expected a SHA-1 Git object ID.'); return value; }
export function httpsRemote(value, hosts) {
    let url; try { url = new URL(value); } catch { throw new GitError('invalid_remote', 'Enter an absolute HTTPS repository URL.'); }
    requireValue(url.protocol === 'https:' && !url.username && !url.password && !url.search && !url.hash && url.pathname !== '/' && (!hosts || hosts.includes(url.hostname)), 'invalid_remote', 'Use an allowed HTTPS remote without embedded credentials, query or fragment.');
    return url;
}
export function githubRemote(value) {
    const url = httpsRemote(value, ['github.com']);
    requireValue(!url.port, 'invalid_remote', 'Use the standard GitHub HTTPS port.');
    const parts = url.pathname.replace(/\/$/, '').replace(/\.git$/, '').split('/').slice(1);
    requireValue(parts.length === 2 && parts.every(p => /^[A-Za-z0-9_.-]+$/.test(p) && p !== '.' && p !== '..'), 'invalid_remote', 'Use https://github.com/owner/repository.');
    return { owner: parts[0], repo: parts[1], url: `https://github.com/${parts.join('/')}.git` };
}
export const utf8 = text => new TextEncoder().encode(text);
export function text(bytes) { requireValue(!bytes.includes(0), 'binary_file', 'This file is binary.'); try { return new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes); } catch { throw new GitError('binary_file', 'This file is not UTF-8 text.'); } }
export function concat(...chunks) { const bytes = new Uint8Array(chunks.reduce((n, c) => n + c.length, 0)); let at = 0; for (const chunk of chunks) { bytes.set(chunk, at); at += chunk.length; } return bytes; }
export function fromHex(hex) { oid(hex); return Uint8Array.from(hex.match(/../g), p => parseInt(p, 16)); }
export async function gitHash(type, bytes) {
    requireValue(['blob', 'tree', 'commit', 'tag'].includes(type) && bytes instanceof Uint8Array, 'invalid_object', 'Invalid Git object.');
    const hash = await crypto.subtle.digest('SHA-1', concat(utf8(`${type} ${bytes.length}\0`), bytes));
    return [...new Uint8Array(hash)].map(x => x.toString(16).padStart(2, '0')).join('');
}
export function base64(bytes) { let value = ''; for (let i = 0; i < bytes.length; i += 8192) value += String.fromCharCode(...bytes.subarray(i, i + 8192)); return btoa(value); }
export function unbase64(value) { return Uint8Array.from(atob(value.replace(/\s/g, '')), c => c.charCodeAt(0)); }
export function equalEntry(a, b) { return a === b || !!a && !!b && a.oid === b.oid && a.mode === b.mode; }
export function changes(left, right) {
    return [...new Set([...left.keys(), ...right.keys()])].sort().filter(p => !equalEntry(left.get(p), right.get(p))).map(path => ({ path, before: left.get(path) ?? null, after: right.get(path) ?? null, kind: !left.has(path) ? 'added' : !right.has(path) ? 'deleted' : 'modified' }));
}
/** Canonical Git tree encoding, including byte ordering and directory suffix ordering. */
export async function buildTrees(files) {
    const root = new Map(), objects = [];
    for (const [path, entry] of files) {
        repositoryPath(path); oid(entry.oid);
        requireValue(['100644', '100755', '120000', '160000'].includes(entry.mode), 'invalid_mode', 'Unsupported Git file mode.');
        const parts = path.split('/'); let node = root;
        for (const part of parts.slice(0, -1)) { if (!node.has(part)) node.set(part, new Map()); requireValue(node.get(part) instanceof Map, 'path_collision', 'A file shadows a directory.'); node = node.get(part); }
        requireValue(!node.has(parts.at(-1)), 'path_collision', 'Duplicate file or directory.'); node.set(parts.at(-1), entry);
    }
    async function visit(node) {
        const entries = [];
        for (const [name, value] of node) entries.push(value instanceof Map ? { name, mode: '40000', oid: await visit(value) } : { name, ...value });
        const order = entry => utf8(entry.name + (entry.mode === '40000' ? '/' : ''));
        entries.sort((a, b) => { const x = order(a), y = order(b); for (let i = 0; i < Math.min(x.length, y.length); i++) if (x[i] !== y[i]) return x[i] - y[i]; return x.length - y.length; });
        const bytes = concat(...entries.map(e => concat(utf8(`${e.mode} ${e.name}\0`), fromHex(e.oid))));
        const id = await gitHash('tree', bytes); objects.push({ oid: id, bytes, entries }); return id;
    }
    return { oid: await visit(root), objects };
}
export function identity(value) {
    requireValue(value && typeof value.name === 'string' && value.name.trim() && !/[<>\r\n\0]/.test(value.name) && typeof value.email === 'string' && /^[^<>\s@]+@[^<>\s@]+$/.test(value.email), 'invalid_identity', 'Enter a name and email for the commit author.');
    const date = new Date(value.date ?? Date.now()); requireValue(Number.isFinite(date.valueOf()), 'invalid_date', 'Invalid commit date.');
    return { name: value.name.trim(), email: value.email, date: new Date(Math.floor(date.valueOf() / 1000) * 1000).toISOString() };
}
export async function makeCommit(tree, parents, message, author) {
    oid(tree); parents.forEach(oid); author = identity(author);
    requireValue(typeof message === 'string' && message.trim() && message.length <= 200000 && !message.includes('\0'), 'invalid_message', 'Enter a bounded, nonempty commit message.');
    message = message.replace(/\n*$/, '\n');
    const who = `${author.name} <${author.email}> ${Math.floor(new Date(author.date).valueOf() / 1000)} +0000`;
    const bytes = utf8(`tree ${tree}\n${parents.map(p => `parent ${p}\n`).join('')}author ${who}\ncommitter ${who}\n\n${message}`);
    return { oid: await gitHash('commit', bytes), tree, parents, message, author, committer: author, bytes };
}
