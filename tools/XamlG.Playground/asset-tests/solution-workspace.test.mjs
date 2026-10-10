import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../wwwroot/solution-workspace.js', import.meta.url), 'utf8');
let identity = 0;
function globals(t, values) {
  for (const [name, value] of Object.entries(values)) {
    const original = Object.getOwnPropertyDescriptor(globalThis, name);
    Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
    t.after(() => original ? Object.defineProperty(globalThis, name, original) : delete globalThis[name]);
  }
}
async function workspace(t, indexedDB) {
  globals(t, { addEventListener() {}, indexedDB });
  return import('data:text/javascript;base64,' + Buffer.from(source + `\n// instance ${identity++}`).toString('base64'));
}
function readableDatabase(value) {
  const db = { closed: false, close() { this.closed = true; }, transaction() {
    return { objectStore() { return { get() {
      const request = { result: value };
      queueMicrotask(() => request.onsuccess());
      return request;
    } }; } };
  } };
  return db;
}
function openDatabase(db) {
  return { open() {
    const request = { result: db };
    queueMicrotask(() => request.onsuccess());
    return request;
  } };
}

test('bounded startup loader loads the actual workspace module and rejects unregistered paths', async t => {
  globals(t, { addEventListener() {} });
  const { importModule } = await import('../wwwroot/startup.js');
  const pending = importModule('solution-workspace.js');
  assert.equal(importModule('solution-workspace.js'), pending);
  const module = await pending;
  for (const name of ['loadWorkspace', 'saveWorkspace', 'chooseImport', 'exportWorkspace', 'nativeRequest', 'installShortcuts'])
    assert.equal(typeof module[name], 'function', name);
  for (const path of ['../solution-workspace.js', 'https://example.invalid/plugin.js', 'unregistered.js'])
    assert.throws(() => importModule(path), /Unknown Studio module/);
});

test('workspace storage retries after synchronous IndexedDB unavailability', async t => {
  const module = await workspace(t, undefined);
  await assert.rejects(module.loadWorkspace(), /IndexedDB is unavailable/);
  globalThis.indexedDB = openDatabase(readableDatabase({ revision: 7 }));
  assert.deepEqual(await module.loadWorkspace(), { revision: 7 });
});

test('workspace storage retries a synchronous security error without replacing the module', async t => {
  const module = await workspace(t, { open() { throw new Error('Denied by browser policy'); } });
  await assert.rejects(module.loadWorkspace(), /Denied by browser policy/);
  globalThis.indexedDB = openDatabase(readableDatabase(null));
  assert.equal(await module.loadWorkspace(), null);
});

test('late success from a blocked open closes the abandoned database connection', async t => {
  let blocked;
  const abandoned = readableDatabase(null), replacement = readableDatabase({ revision: 2 });
  const module = await workspace(t, { open() {
    blocked = { result: abandoned };
    queueMicrotask(() => blocked.onblocked());
    return blocked;
  } });
  await assert.rejects(module.loadWorkspace(), /blocking/);
  globalThis.indexedDB = openDatabase(replacement);
  assert.deepEqual(await module.loadWorkspace(), { revision: 2 });
  blocked.onsuccess();
  assert.equal(abandoned.closed, true);
  assert.equal(replacement.closed, false);
  assert.deepEqual(await module.loadWorkspace(), { revision: 2 });
});

test('version change closes and retires the current connection', async t => {
  const first = readableDatabase({ revision: 1 });
  const module = await workspace(t, openDatabase(first));
  assert.deepEqual(await module.loadWorkspace(), { revision: 1 });
  first.onversionchange();
  assert.equal(first.closed, true);
  globalThis.indexedDB = openDatabase(readableDatabase({ revision: 2 }));
  assert.deepEqual(await module.loadWorkspace(), { revision: 2 });
});

test('snapshot bounds and unsafe revision counters are rejected before opening storage', async t => {
  let opened = false;
  const module = await workspace(t, { open() { opened = true; throw new Error('Unexpected storage access'); } });
  for (const revision of [-1, 0.5, NaN, Infinity, Number.MAX_SAFE_INTEGER, Number.MAX_SAFE_INTEGER + 1])
    await assert.rejects(module.saveWorkspace({ format: 1, files: [] }, revision), /Invalid workspace snapshot/);
  for (const state of [null, {}, { format: 2, files: [] }, { format: 1, files: new Array(20001) }])
    await assert.rejects(module.saveWorkspace(state, 0), /Invalid workspace snapshot/);
  for (const file of [null, {}, { path: 'a', content: 1 }, { path: 'a', content: 'a'.repeat(4 * 1024 * 1024 + 1) }])
    await assert.rejects(module.saveWorkspace({ format: 1, files: [file] }, 0), /content limit/);
  assert.equal(opened, false);
});

test('workspace native dispatch and editor history reject unsupported operations', async t => {
  const module = await workspace(t, undefined);
  for (const action of [null, 'agent_run', '../workspace_read'])
    await assert.rejects(module.nativeRequest(action, {}), /Invalid workspace action/);
  await assert.rejects(module.editorHistory('App.cs', 'execute'), /Unsupported history operation/);
});
