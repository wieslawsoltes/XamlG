import test from 'node:test';
import assert from 'node:assert/strict';
import { executionIntegrity } from '../wwwroot/ui-execution-policy.js';

const hash = 'sha256-' + Buffer.alloc(32, 1).toString('base64');
const otherHash = 'sha256-' + Buffer.alloc(32, 2).toString('base64');
const config = { resources: { wasmNative: [{ name: 'dotnet.native.a.wasm' }] } };

test('requires native integrity and retains exact original hashes for browser Fetch SRI', () => {
  const value = { resources: {
    wasmNative: [{ name: 'dotnet.native.a.wasm', hash }],
    coreAssembly: [{ name: 'System.Private.CoreLib.a.wasm', hash: otherHash }]
  } };
  assert.deepEqual([...executionIntegrity(value)], [
    ['_framework/dotnet.native.a.wasm', hash],
    ['_framework/System.Private.CoreLib.a.wasm', otherHash]
  ]);
  assert.throws(() => executionIntegrity(config), /integrity is required/);
  assert.throws(() => executionIntegrity({ resources: {} }), /Exactly one/);
});

test('rejects ambiguous or malformed integrity before any worker asset download', () => {
  for (const invalid of [null, '', 'sha256-wrong', hash + ' ' + otherHash, 42, 'sha384-' + hash.slice(7)]) {
    const value = { resources: { wasmNative: [{ name: 'dotnet.native.a.wasm', hash: invalid }] } };
    assert.throws(() => executionIntegrity(value));
  }
  assert.throws(() => executionIntegrity({ resources: {
    wasmNative: [{ name: 'dotnet.native.a.wasm', hash }],
    assembly: [{ name: 'dotnet.native.a.wasm', hash: otherHash }]
  } }), /Conflicting/);
  assert.throws(() => executionIntegrity({ resources: {
    wasmNative: [{ name: '../escape.wasm', hash }]
  } }), /descriptor/);
});
