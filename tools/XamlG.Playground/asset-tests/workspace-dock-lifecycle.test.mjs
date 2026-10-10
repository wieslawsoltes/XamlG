import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const source = await readFile(new URL('../wwwroot/studio.js', import.meta.url), 'utf8');
const first = source.indexOf('export function dockyardContents(');
const last = source.indexOf('\nlet automationOwner =', first);
assert.ok(first >= 0 && last > first);
// Execute the actual production lifecycle functions without booting Monaco/Blazor.
const { installDockyardWorkspace, reconcileDockDocuments } = runInNewContext(
  source.slice(first, last).replaceAll('export function ', 'function ') +
  '\n({ installDockyardWorkspace, reconcileDockDocuments });', { console });
const flush = () => new Promise(resolve => setImmediate(resolve));
function signal() {
  const listeners = new Set();
  return { add(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    fire(args) { for (const fn of listeners) fn(null, args); } };
}
function fixture(id = 'workspace:identity:App.cs') {
  const items = new Map(); const released = [];
  const manager = {
    DocumentClosing: signal(), AnchorableClosing: signal(), AnchorableHiding: signal(), ActiveContentChanged: signal(),
    Layout: { Descendents: () => items.values() }, Find: key => items.get(key),
    Transaction: (_name, action) => action(), ReleaseContent: key => released.push(key),
    Close(item) { const args = { Model: item, Cancel: false }; this.DocumentClosing.fire(args); if (!args.Cancel) items.delete(item.ContentId); return !args.Cancel; }
  };
  function add(key) { const item = { ContentId: key, Parent: { RemoveChild: model => items.delete(model.ContentId) } }; items.set(key, item); return item; }
  return { manager, released, items, add, item: add(id) };
}

test('workspace tab close waits for managed approval, coalesces requests and honors cancellation', async () => {
  const { manager, item, items } = fixture();
  const calls = []; let answer;
  const hooks = installDockyardWorkspace(manager, { invokeMethodAsync(method, id) {
    calls.push([method, id]); return new Promise(resolve => { answer = resolve; });
  } });
  assert.equal(manager.Close(item), false); assert.equal(manager.Close(item), false);
  assert.deepEqual(calls, [['PrepareDockContentClose', item.ContentId]]);
  answer(false); await flush(); assert.equal(items.has(item.ContentId), true);
  manager.Close(item); assert.equal(calls.length, 2);
  answer(true); await flush(); assert.equal(items.has(item.ContentId), false);
  hooks.dispose();
});

test('late close approval cannot close a replacement document or a disposed owner', async () => {
  for (const retire of ['replace', 'dispose']) {
    const { manager, item, add, items } = fixture(); let answer;
    const hooks = installDockyardWorkspace(manager, { invokeMethodAsync: () => new Promise(resolve => { answer = resolve; }) });
    manager.Close(item);
    if (retire === 'replace') add(item.ContentId); else hooks.dispose();
    answer(true); await flush(); assert.equal(items.has(item.ContentId), true);
    hooks.dispose();
  }
});

test('retiring a workspace removes obsolete panes and releases content without touching current documents or tools', () => {
  const { manager, item, add, items, released } = fixture();
  add('workspace:identity:Keep.cs'); add('document:View.axaml'); add('agent'); add('git:independent-document');
  const current = ['workspace:identity:Keep.cs', 'document:View.axaml'];
  reconcileDockDocuments(manager, current, [item.ContentId, ...current]);
  assert.equal(items.has(item.ContentId), false);
  assert.deepEqual([...items.keys()].sort(), ['agent', 'git:independent-document', ...current].sort());
  assert.deepEqual(released, [item.ContentId]);
});
