import test from 'node:test';
import assert from 'node:assert/strict';
import { readExecutionConfig, executionAssets, constrainWasmMemory } from '../wwwroot/ui-execution-policy.js';

const config = { resources: {
  wasmNative: [{ name: 'dotnet.native.a.wasm' }], jsModuleNative: [{ name: 'dotnet.native.a.js' }],
  jsModuleRuntime: [{ name: 'dotnet.runtime.a.js' }], coreAssembly: [{ name: 'System.Private.CoreLib.a.wasm' }]
} };
const inventory = ['_framework/dotnet.js','_framework/dotnet.native.a.wasm','_framework/dotnet.native.a.js',
  '_framework/dotnet.runtime.a.js','_framework/System.Private.CoreLib.a.wasm','references/index.txt','references/System.Runtime.dll',
  '_framework/sw.js','_framework/fr/Microsoft.CodeAnalysis.resources.a.wasm'];
const header=[0,97,115,109,1,0,0,0];
const leb = value => { const result=[]; do { const part=value%128;value=Math.floor(value/128);result.push(part|(value?128:0)); } while(value);return result; };
const section = payload => [5,...leb(payload.length),...payload];
const memoryModule = (initial,maximum) => new Uint8Array([...header,...section([1,maximum===null?0:1,...leb(initial),...(maximum===null?[]:leb(maximum))]),
  7,10,1,6,109,101,109,111,114,121,2,0]).buffer;

test('reads embedded .NET 10 configuration as JSON, never executes loader code',()=>{
  globalThis.executedManifest=false;
  const text='throw new Error("not executed"); dotnet.withConfig(/*json-start*/'+JSON.stringify(config)+'/*json-end*/);globalThis.executedManifest=true;';
  assert.deepEqual(readExecutionConfig(text),config);assert.equal(globalThis.executedManifest,false);delete globalThis.executedManifest;
});
test('accepts standalone boot JSON and exported JSON configuration',()=>{
  assert.deepEqual(readExecutionConfig(JSON.stringify(config)),config);
  assert.deepEqual(readExecutionConfig('export const config = '+JSON.stringify(config)+';'),config);
});
test('rejects ambiguous, executable, incomplete and oversized manifest data',()=>{
  for(const source of ['/*json-start*/{}','/*json-start*/{}/*json-end*//*json-start*/{}/*json-end*/','export const config = (()=>({}))();','null','{}',' '.repeat(2097153)])
    assert.throws(()=>readExecutionConfig(source));
});
test('selects exact declared runtime and compiler assets without unrelated scripts or satellites',()=>{
  const selected=executionAssets(config,inventory,'_framework/dotnet.js');
  assert.equal(selected.length,7);assert.ok(!selected.some(path=>path.endsWith('/sw.js')||path.includes('/fr/')));
  assert.throws(()=>executionAssets(config,inventory.filter(path=>!path.endsWith('.dll')&&!path.endsWith('index.txt')),'_framework/dotnet.js'));
  assert.throws(()=>executionAssets(config,[...inventory,'_framework/../escape.js'],'_framework/dotnet.js'));
  assert.throws(()=>executionAssets(config,[...inventory,inventory[0]],'_framework/dotnet.js'));
  assert.throws(()=>executionAssets({resources:{...config.resources,assembly:[{name:'missing.wasm'}]}},inventory,'_framework/dotnet.js'));
});
test('the engine enforces the narrowed defined-memory maximum',()=>{
  const original=memoryModule(1,32768), copy=new Uint8Array(original).slice();
  const bounded=constrainWasmMemory(original,4);
  assert.deepEqual(new Uint8Array(original),copy);assert.equal(bounded.maximumPages,4);
  const instance=new WebAssembly.Instance(new WebAssembly.Module(bounded.bytes));
  assert.equal(instance.exports.memory.grow(3),1);
  assert.throws(()=>instance.exports.memory.grow(1),RangeError);
  assert.equal(instance.exports.memory.buffer.byteLength,4*65536);
});
test('supports missing maximum, preserves a stricter maximum and rejects an excessive minimum',()=>{
  assert.equal(constrainWasmMemory(memoryModule(1,null),8).maximumPages,8);
  assert.equal(constrainWasmMemory(memoryModule(1,2),8).maximumPages,2);
  assert.throws(()=>constrainWasmMemory(memoryModule(9,10),8));
});
test('fails closed on malformed, multiple, shared and memory64 memory declarations',()=>{
  for(const bytes of [[],header,[...header,5,9,1],[...header,...section([2,1,1,4,1,1,4])],
    [...header,...section([1,3,1,4])],[...header,...section([1,5,1,4])],
    [...header,...section([1,1,1,4]),...section([1,1,1,4])],
    [...header,5,255,255,255,255,16], [...header,...section([1,1,5,4])]])
    assert.throws(()=>constrainWasmMemory(new Uint8Array(bytes).buffer,4));
});
