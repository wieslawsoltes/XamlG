import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const workspaceSource = await readFile(new URL('../wwwroot/solution-workspace.js', import.meta.url), 'utf8');
const studioSource = await readFile(new URL('../wwwroot/studio.js', import.meta.url), 'utf8');
let sequence = 0;
function replaceGlobal(t, name, value) {
  const previous = Object.getOwnPropertyDescriptor(globalThis, name);
  Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
  t.after(() => previous ? Object.defineProperty(globalThis, name, previous) : delete globalThis[name]);
}
async function workspace(t, importModule) {
  replaceGlobal(t, 'addEventListener', () => {});
  replaceGlobal(t, 'xamlgBoot', { importModule });
  return import('data:text/javascript;base64,' + Buffer.from(workspaceSource + `\n// cancellation fixture ${sequence++}`).toString('base64'));
}
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
function studio(fetcher, parent = new AbortController()) {
  const first = studioSource.indexOf('export async function agentRequest(');
  const last = studioSource.indexOf('\nasync function startAgentStream()', first);
  assert.ok(first >= 0 && last > first);
  const request = runInNewContext(studioSource.slice(first, last).replace('export async function', 'async function') + '\nagentRequest', {
    AbortController, Set, encodeURIComponent, fetch: fetcher,
    agentConnection: { base: 'http://127.0.0.1:4893', token: 'owner-test-token', session: 'session-test-id' }, agentStream: parent
  });
  return { request, parent };
}

test('cancellation while the Studio module loads does not start a native request', async t => {
  const loading = deferred(); let calls = 0;
  const api = await workspace(t, () => loading.promise);
  const pending = api.nativeRequest('workspace_build', {}, 'request-1');
  const rejected = assert.rejects(pending, { name: 'AbortError' });
  assert.equal(api.cancelNativeRequest('request-1'), true);
  assert.equal(api.cancelNativeRequest('request-1'), false);
  loading.resolve({ agentRequest() { calls++; } });
  await rejected; assert.equal(calls, 0);
  assert.equal(api.cancelNativeRequest('request-1'), false);
});

test('cancellation is scoped to a request, not another workspace or the companion', async t => {
  const active = new Map();
  const api = await workspace(t, async () => ({ agentRequest: (action, args, { signal }) => {
    const pending = deferred(); active.set(args.id, { ...pending, signal });
    signal.addEventListener('abort', () => pending.reject(signal.reason), { once: true });
    return pending.promise;
  } }));
  const first = api.nativeRequest('workspace_build', { id: 1 }, 'build-1');
  const second = api.nativeRequest('workspace_evaluate', { id: 2 }, 'evaluate-2');
  const rejected = assert.rejects(first, { name: 'AbortError' });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(api.cancelNativeRequest('build-1'), true);
  assert.equal(active.get(2).signal.aborted, false);
  active.get(2).resolve({ evaluated: true });
  await rejected; assert.deepEqual(await second, { evaluated: true });
  assert.equal(api.cancelNativeRequest('evaluate-2'), false);
});

test('request identities reject duplicates and are released after failures', async t => {
  const response = deferred();
  const api = await workspace(t, async () => ({ agentRequest: () => response.promise }));
  const first = api.nativeRequest('workspace_templates', {}, 'same-id');
  const failed = assert.rejects(first, /Provider failure/);
  await assert.rejects(api.nativeRequest('workspace_templates', {}, 'same-id'), /already running/);
  for (const id of ['', 'x'.repeat(65), {}, '../outside'])
    await assert.rejects(api.nativeRequest('workspace_templates', {}, id), /Invalid workspace request identity/);
  response.reject(new Error('Provider failure')); await failed;
  assert.equal(api.cancelNativeRequest('same-id'), false);
});

test('a provider ignoring cancellation cannot publish a late successful result', async t => {
  const response = deferred();
  const api = await workspace(t, async () => ({ agentRequest: () => response.promise }));
  const pending = api.nativeRequest('workspace_evaluate', {}, 'slow-result');
  const rejected = assert.rejects(pending, { name: 'AbortError' });
  await new Promise(resolve => setImmediate(resolve));
  api.cancelNativeRequest('slow-result'); response.resolve({ evaluated: true });
  await rejected;
});

test('agent HTTP cancellation links owner revocation without aborting sibling requests', async () => {
  const requests = [];
  const { request, parent } = studio(async (url, options) => {
    assert.match(url, /^http:\/\/127\.0\.0\.1:4893\/agent\/workspace_/);
    assert.equal(options.headers['X-Xamlg-Owner-Session'], 'session-test-id');
    const pending = deferred(); requests.push({ ...pending, signal: options.signal });
    options.signal.addEventListener('abort', () => pending.reject(options.signal.reason), { once: true });
    return pending.promise;
  });
  const operation = new AbortController();
  const first = request('workspace_build', {}, { signal: operation.signal });
  const second = request('workspace_status');
  const a = assert.rejects(first, { name: 'AbortError' });
  const b = assert.rejects(second, { name: 'AbortError' });
  operation.abort(); await a;
  assert.equal(parent.signal.aborted, false);
  assert.equal(requests[1].signal.aborted, false);
  parent.abort(); await b;
});

test('agent HTTP abort listeners are removed after successful and failed responses', async () => {
  for (const ok of [true, false]) {
    const parent = new AbortController(), operation = new AbortController();
    const counts = new Map();
    for (const signal of [parent.signal, operation.signal]) {
      counts.set(signal, 0);
      const add = signal.addEventListener.bind(signal), remove = signal.removeEventListener.bind(signal);
      signal.addEventListener = (...args) => { counts.set(signal, counts.get(signal) + 1); return add(...args); };
      signal.removeEventListener = (...args) => { counts.set(signal, counts.get(signal) - 1); return remove(...args); };
    }
    const { request } = studio(async () => ({ ok, status: ok ? 200 : 400, text: async () => JSON.stringify(ok ? { exitCode: 0 } : { error: 'Rejected' }) }), parent);
    const pending = request('workspace_build', {}, { signal: operation.signal });
    if (ok) assert.equal((await pending).exitCode, 0); else await assert.rejects(pending, /Rejected/);
    assert.deepEqual([...counts.values()], [0, 0]);
  }
});

test('an already aborted request never reaches fetch', async () => {
  let calls = 0;
  const controller = new AbortController(); controller.abort();
  const { request } = studio(() => { calls++; });
  await assert.rejects(request('workspace_build', {}, { signal: controller.signal }), { name: 'AbortError' });
  assert.equal(calls, 0);
});
