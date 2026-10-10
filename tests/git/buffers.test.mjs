import test from 'node:test';
import assert from 'node:assert/strict';
import { editBuffer, saveFileBuffer, loadFileBuffer, captureFileBuffer } from '../../tools/XamlG.Playground/wwwroot/git/buffers.mjs';
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const file = (value, oid = 'next') => ({ bytes: new TextEncoder().encode(value), entry: { oid, mode: '100644' } });
const buffer = repository => ({ repository, path: 'file.txt', text: 'initial', version: 'old', mode: '100644', loaded: true, binary: false, dirty: true });
test('typing during save keeps the newer editor contents dirty', async () => {
    const gate = deferred(), calls = [], state = buffer({ write: (...args) => { calls.push(args); return gate.promise; } });
    const pending = saveFileBuffer(state); editBuffer(state, 'newer typing'); gate.resolve('saved'); await pending;
    assert.equal(calls[0][1], 'initial'); assert.equal(calls[0][2].expectedOid, 'old');
    assert.equal(state.version, 'saved'); assert.equal(state.text, 'newer typing'); assert.equal(state.dirty, true);
});
test('successful save clears only an unchanged submitted snapshot', async () => {
    const state = buffer({ write: async () => 'saved' }); await saveFileBuffer(state); assert.equal(state.dirty, false); assert.equal(state.version, 'saved');
});
test('failed save preserves text, dirty state and expected repository version', async () => {
    const state = buffer({ write: async () => { throw new Error('network'); } }); await assert.rejects(saveFileBuffer(state));
    assert.equal(state.text, 'initial'); assert.equal(state.dirty, true); assert.equal(state.version, 'old');
});
test('successful save cannot adopt a stale version after the buffer was retargeted', async () => {
    const gate = deferred(), state = buffer({ write: () => gate.promise }); const pending = saveFileBuffer(state);
    state.version = 'external'; gate.resolve('saved'); await assert.rejects(pending, { code: 'buffer_changed' }); assert.equal(state.version, 'external');
});
test('typing during reload rejects the late file response without changing the buffer', async () => {
    const gate = deferred(), state = buffer({ read: () => gate.promise }); const pending = loadFileBuffer(state);
    editBuffer(state, 'keystrokes'); gate.resolve(file('remote')); await assert.rejects(pending, { code: 'stale_load' });
    assert.equal(state.text, 'keystrokes'); assert.equal(state.version, 'old'); assert.equal(state.dirty, true);
});
test('a newer reload supersedes an earlier request even if the earlier response arrives last', async () => {
    const one = deferred(), two = deferred(); let count = 0; const state = buffer({ read: () => ++count === 1 ? one.promise : two.promise });
    const first = loadFileBuffer(state), second = loadFileBuffer(state); two.resolve(file('newest', 'two')); await second;
    one.resolve(file('older', 'one')); await assert.rejects(first, { code: 'stale_load' }); assert.equal(state.text, 'newest'); assert.equal(state.version, 'two');
});
test('reload preserves exact UTF-8 BOM and line endings and clears a confirmed dirty buffer', async () => {
    const content = '\uFEFFone\r\ntwo\nlast', state = buffer({ read: async () => file(content) }); await loadFileBuffer(state);
    assert.equal(state.text, content); assert.equal(state.dirty, false); assert.equal(state.binary, false);
});
test('binary loads expose a bounded preview and cannot be saved as text', async () => {
    const state = buffer({ read: async () => ({ ...file(''), bytes: Uint8Array.of(0, 255) }) }); await loadFileBuffer(state);
    assert.equal(state.binary, true); assert.match(state.text, /00 ff/); await assert.rejects(saveFileBuffer(state), { code: 'invalid_buffer' });
});
test('Studio capture cannot overwrite keystrokes entered while the host is responding', async () => {
    const gate = deferred(), state = buffer({}); const pending = captureFileBuffer(state, () => gate.promise);
    editBuffer(state, 'typing'); gate.resolve('Studio'); await assert.rejects(pending, { code: 'buffer_changed' }); assert.equal(state.text, 'typing');
});
test('Studio capture marks new text dirty without altering the repository version', async () => {
    const state = buffer({}); state.dirty = false; await captureFileBuffer(state, async () => 'Studio');
    assert.equal(state.text, 'Studio'); assert.equal(state.dirty, true); assert.equal(state.version, 'old');
});
