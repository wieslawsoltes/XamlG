import test from 'node:test';
import assert from 'node:assert/strict';
import { mergeText } from '../../tools/XamlG.Playground/wwwroot/git/merge.mjs';
import { MemoryWorkspaceStore } from '../../tools/XamlG.Playground/wwwroot/git/storage.mjs';
import { VirtualRepository } from '../../tools/XamlG.Playground/wwwroot/git/virtual.mjs';
const author = { name: 'Tests', email: 'tests@example.test', date: '2026-10-10T00:00:00Z' };
const decode = result => new TextDecoder('utf-8', { ignoreBOM: true }).decode(result.bytes);
test('diff3 merges independent same-file edits without losing exact line endings', () => {
    const base = '\uFEFFzero\r\none\ntwo\r\nthree', ours = base.replace('one', 'OUR'), theirs = base.replace('three', 'THEIR');
    assert.deepEqual(mergeText(base, ours, theirs), { clean: true, text: base.replace('one', 'OUR').replace('three', 'THEIR'), conflicts: [] });
});
test('diff3 reports competing edits and boundary insertions explicitly', () => {
    assert.equal(mergeText('base\n', 'ours\n', 'theirs\n').clean, false);
    assert.equal(mergeText('line\n', 'left\nline\n', 'right\nline\n').clean, false);
    assert.equal(mergeText('line\n', 'insert\nline\n', 'replace\n').clean, false);
});
test('diff3 deduplicates identical edits and handles deletions and empty input', () => {
    assert.equal(mergeText('a\nb\nc\n', 'a\nB\nc\n', 'a\nB\nc\n').text, 'a\nB\nc\n');
    assert.equal(mergeText('a\nb\nc\nd\n', 'b\nc\nd\n', 'a\nb\nc\nD\n').text, 'b\nc\nD\n');
    assert.equal(mergeText('', 'new\n', '').text, 'new\n');
});
test('diff3 keeps independent insertions and adjacent replacements ordered', () => {
    assert.equal(mergeText('a\nb\nc\n', 'x\na\nb\nc\n', 'a\nb\nc\ny\n').text, 'x\na\nb\nc\ny\n');
    assert.equal(mergeText('a\nb\nc\n', 'a\nB\nc\n', 'a\nb\nC\n').text, 'a\nB\nC\n');
});
test('diff3 composes a corpus of disjoint edit intervals', () => {
    const values = Array.from({ length: 80 }, (_, i) => `unique ${i}\n`), base = values.join('');
    for (let i = 0; i < 30; i++) {
        const ours = values.slice(), theirs = values.slice(), expected = values.slice();
        ours[i] = expected[i] = `ours ${i}\n`; theirs[79 - i] = expected[79 - i] = `theirs ${i}\n`;
        assert.equal(mergeText(base, ours.join(''), theirs.join('')).text, expected.join(''));
    }
});
async function fixture() {
    const store = new MemoryWorkspaceStore(), repo = await VirtualRepository.create(store, null), base = Array.from({ length: 20 }, (_, i) => `line ${i}\n`).join('');
    await repo.write('a.txt', base); await repo.stage(['a.txt']); await repo.commit('base', author); return { store, repo, base };
}
test('virtual merge combines separate same-file changes into a real merge commit', async () => {
    const { repo, base } = await fixture(); await repo.createBranch('feature');
    await repo.write('a.txt', base.replace('line 1\n', 'ours\n')); await repo.stage(['a.txt']); const ours = await repo.commit('ours', author);
    await repo.checkout('feature'); await repo.write('a.txt', base.replace('line 18\n', 'theirs\n')); await repo.stage(['a.txt']); const theirs = await repo.commit('theirs', author);
    await repo.checkout('main'); assert.deepEqual((await repo.merge(theirs)).conflicts, []);
    assert.equal(decode(await repo.read('a.txt')), base.replace('line 1\n', 'ours\n').replace('line 18\n', 'theirs\n'));
    const id = await repo.commit('merge', author); assert.deepEqual(repo.state.commits.get(id).parents, [ours, theirs]);
});
test('cross-base virtual stash restore preserves staged and unstaged layers', async () => {
    const { repo, base } = await fixture();
    const index = base.replace('line 1\n', 'staged\n'), work = index.replace('line 5\n', 'unstaged\n');
    await repo.write('a.txt', index); await repo.stage(['a.txt']); await repo.write('a.txt', work); const stash = await repo.stash('both layers');
    await repo.write('a.txt', base.replace('line 18\n', 'upstream\n')); await repo.stage(['a.txt']); await repo.commit('upstream', author);
    await repo.applyStash(stash, true);
    assert.equal(decode(await repo.read('a.txt', 'index')), index.replace('line 18\n', 'upstream\n'));
    assert.equal(decode(await repo.read('a.txt')), work.replace('line 18\n', 'upstream\n')); assert.equal(repo.state.stashes.length, 0);
});
test('conflicting cross-base stash pop preserves stash, index and working tree atomically', async () => {
    const { repo, base } = await fixture(); await repo.write('a.txt', base.replace('line 1\n', 'stash\n')); const stash = await repo.stash();
    await repo.write('a.txt', base.replace('line 1\n', 'branch\n')); await repo.stage(['a.txt']); await repo.commit('branch', author);
    const before = repo.state; await assert.rejects(repo.applyStash(stash, true), { code: 'stash_conflicts' }); assert.deepEqual(repo.state, before);
});
