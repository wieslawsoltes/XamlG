import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, chmod } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { NativeGit } from '../../tools/git-companion/native.mjs';
import { NativeRepository } from '../../tools/XamlG.Playground/wwwroot/git/native-client.mjs';
const author = { name: 'Tests', email: 'tests@example.test' };
async function fixture(t) {
    const root = await mkdtemp(join(tmpdir(), 'xamlg-diff-')), git = new NativeGit(root), info = await git.init();
    t.after(() => rm(root, { recursive: true, force: true }));
    const repo = join(root, 'repos', info.id, 'worktree'), run = (op, args) => git.execute(info.id, op, args);
    const write = async (path, value) => { let version = null; try { version = (await run('read', { path })).version; } catch (e) { if (e.code !== 'ENOENT') throw e; }
        return run('write', { path, base64: Buffer.from(value).toString('base64'), expectedVersion: version }); };
    const commit = async paths => { await run('stage', { paths }); await run('commit', { ...author, message: 'Commit' }); };
    return { root, git, info, repo, run, write, commit, raw: (...args) => execFileSync('git', args, { cwd: repo }).toString('utf8') };
}
test('native snapshot returns status, refs, files, history and stashes in one operation', async t => {
    const f = await fixture(t); await f.write('a.txt', 'a'); await f.commit(['a.txt']);
    const snapshot = await f.run('snapshot'); assert.equal(snapshot.status.entries.length, 0);
    assert.deepEqual(snapshot.files, ['a.txt']); assert.equal(snapshot.history[0].oid, snapshot.status.head);
    assert.equal(snapshot.refs[0].oid, snapshot.status.head); assert.equal(snapshot.stashes.text, '');
});
test('native hunk staging preserves index/worktree separation and exact CRLF/BOM bytes', async t => {
    const f = await fixture(t), before = '\uFEFF' + Array.from({ length: 25 }, (_, i) => `line ${i}\r\n`).join('');
    const after = before.replace('line 1\r\n', 'first change\r\n').replace('line 23\r\n', 'second change\r\n');
    await f.write('text.txt', before); await f.commit(['text.txt']); await f.write('text.txt', after);
    const diff = await f.run('diffview', { path: 'text.txt' }); assert.equal(diff.before, before); assert.equal(diff.after, after); assert.equal(diff.hunks.length, 2);
    await f.run('stagehunks', { path: 'text.txt', selected: [diff.hunks[0].id], token: diff.token });
    assert.equal(f.raw('show', ':text.txt'), before.replace('line 1\r\n', 'first change\r\n'));
    assert.equal(Buffer.from((await f.run('read', { path: 'text.txt' })).base64, 'base64').toString('utf8'), after);
    const staged = await f.run('diffview', { path: 'text.txt', staged: true }); assert.equal(staged.before, before); assert.equal(staged.hunks.length, 1);
});
test('native hunk staging rejects a changed working file', async t => {
    const f = await fixture(t); await f.write('a', 'base\n'); await f.commit(['a']); await f.write('a', 'first\n');
    const diff = await f.run('diffview', { path: 'a' }); await f.write('a', 'second\n');
    await assert.rejects(f.run('stagehunks', { path: 'a', selected: [0], token: diff.token }), { code: 'stale_hunk' });
    assert.equal(f.raw('show', ':a'), 'base\n');
});
test('native hunk staging rejects a changed index even when worktree is unchanged', async t => {
    const f = await fixture(t); await f.write('a', 'base\n'); await f.commit(['a']); await f.write('a', 'new\n');
    const diff = await f.run('diffview', { path: 'a' }); await f.run('stage', { paths: ['a'] });
    await assert.rejects(f.run('stagehunks', { path: 'a', selected: [0], token: diff.token }), { code: 'stale_hunk' });
});
test('native hunk staging supports new files with literal metacharacter paths', async t => {
    const f = await fixture(t), path = 'literal[one]*.txt'; await f.write(path, 'new file without newline');
    const diff = await f.run('diffview', { path }); assert.equal(diff.before, '');
    await f.run('stagehunks', { path, selected: [0], token: diff.token });
    assert.equal(f.raw('show', `:${path}`), 'new file without newline');
});
test('native hunk staging can stage the complete deletion', async t => {
    const f = await fixture(t); await f.write('a', 'base\n'); await f.commit(['a']); await rm(join(f.repo, 'a'));
    const diff = await f.run('diffview', { path: 'a' }); assert.equal(diff.after, ''); assert.equal(diff.token.after, null);
    await f.run('stagehunks', { path: 'a', selected: [0], token: diff.token });
    assert.equal(f.raw('ls-files', '--', 'a'), ''); assert.equal((await f.run('status')).entries[0].code, 'D ');
});
test('native binary diffs do not expose a text editor or hunk staging', async t => {
    const f = await fixture(t); await f.write('a', Buffer.from([1, 0, 255]));
    const diff = await f.run('diffview', { path: 'a' }); assert.equal(diff.binary, true); assert.equal(diff.before, undefined);
    await assert.rejects(f.run('stagehunks', { path: 'a', selected: [0], token: {} }), { code: 'stale_hunk' });
});
test('native mode changes invalidate a hunk token', { skip: process.platform === 'win32' }, async t => {
    const f = await fixture(t); await f.write('a', 'base\n'); await f.commit(['a']); await f.write('a', 'new\n');
    const diff = await f.run('diffview', { path: 'a' }); await chmod(join(f.repo, 'a'), 0o755);
    await assert.rejects(f.run('stagehunks', { path: 'a', selected: [0], token: diff.token }), { code: 'stale_hunk' });
});
test('native adapter refresh uses a single consistent snapshot request', async t => {
    const f = await fixture(t), calls = [], client = { call: async (action, args) => { calls.push(args.operation); return f.git.execute(args.id, args.operation, args.args); } };
    const repo = new NativeRepository(client, f.info); await repo.refresh(); assert.deepEqual(calls, ['snapshot']);
});
test('native adapter preserves the operation failure when refresh also fails', async () => {
    const original = Object.assign(new Error('original write failure'), { code: 'git_failed' });
    const repo = new NativeRepository({ call: async (_action, args) => { if (args.operation === 'snapshot') throw new Error('refresh failed'); throw original; } }, { id: 'test' });
    await assert.rejects(repo.stage(['a']), error => error === original && error.refreshError === 'refresh failed');
});
test('native adapter reports completed writes separately from failed refreshes', async () => {
    const repo = new NativeRepository({ call: async (_action, args) => { if (args.operation === 'snapshot') throw new Error('refresh failed'); return { ok: true }; } }, { id: 'test' });
    await assert.rejects(repo.stage(['a']), error => error.code === 'refresh_failed' && error.details.completed === true);
});
