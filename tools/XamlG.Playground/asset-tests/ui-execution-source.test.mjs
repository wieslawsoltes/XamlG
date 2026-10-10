import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Script, createContext, runInContext } from 'node:vm';
import { readFileSync } from 'node:fs';
import { buildWorkerSource } from '../wwwroot/ui-execution-worker-client.js';

 test('classic worker source keeps captured transport and policy out of global lexical scope', () => {
  const context = createContext({});
  const source = buildWorkerSource('export const value = 42;', 'const reply = 123; globalThis.observed = executionPolicySource;');
  new Script(source).runInContext(context);
  assert.equal(context.observed, 'export const value = 42;');
  assert.equal(runInContext('typeof reply', context), 'undefined');
  assert.equal(runInContext('typeof executionPolicySource', context), 'undefined');
});

 test('policy bytes remain a quoted string rather than executable classic script', () => {
  const policy = '\"; globalThis.injected = true; //\nexport const closing = "})();";';
  const context = createContext({});
  new Script(buildWorkerSource(policy, 'globalThis.observed = executionPolicySource;')).runInContext(context);
  assert.equal(context.observed, policy);
  assert.equal(context.injected, undefined);
});

 test('actual worker bundle parses as classic JavaScript and obeys the supervisor source limit', () => {
  const read = name => readFileSync(new URL('../wwwroot/' + name, import.meta.url), 'utf8');
  const source = buildWorkerSource(read('ui-execution-policy.js'), read('ui-execution-worker.js'));
  assert.doesNotThrow(() => new Script(source));
  assert.ok(source.length < 131072);
  assert.throws(() => buildWorkerSource(null, ''), /Invalid/);
  assert.throws(() => buildWorkerSource('x'.repeat(131072), 'x'), /bound/);
});
