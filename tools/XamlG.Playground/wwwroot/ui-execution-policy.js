// Trusted .NET runtime preparation. This module never evaluates manifest JavaScript.
// Memory-section format: https://webassembly.github.io/spec/core/binary/modules.html#memory-section
const runtimeGroups = Object.freeze({
  wasmNative: 'dotnetwasm', jsModuleNative: 'js-module-native', jsModuleRuntime: 'js-module-runtime',
  coreAssembly: 'assembly', assembly: 'assembly', lazyAssembly: 'assembly',
  coreVfs: 'vfs', vfs: 'vfs', icu: 'icu'
});

export function readExecutionConfig(source) {
  if (typeof source !== 'string' || source.length > 2097152) throw new Error('Invalid runtime manifest size.');
  const start = '/*json-start*/', end = '/*json-end*/';
  const first = source.indexOf(start);
  let json;
  if (first >= 0) {
    const last = source.indexOf(end, first + start.length);
    if (last < 0 || source.indexOf(start, first + start.length) >= 0 || source.indexOf(end, last + end.length) >= 0)
      throw new Error('Ambiguous or incomplete embedded .NET configuration.');
    json = source.slice(first + start.length, last);
  } else {
    const text = source.trim().replace(/^\uFEFF/, '');
    // Standalone .NET boot JSON and the public `export const config = <JSON>` format.
    json = text.startsWith('{') ? text : /^export\s+const\s+config\s*=\s*([\s\S]*?);?\s*$/.exec(text)?.[1];
  }
  if (!json) throw new Error('The published .NET loader contains no supported boot configuration.');
  const config = JSON.parse(json);
  if (!config || typeof config !== 'object' || Array.isArray(config) || !config.resources ||
      typeof config.resources !== 'object' || Array.isArray(config.resources)) throw new Error('Invalid trusted .NET boot configuration.');
  for (const name of ['wasmNative', 'jsModuleNative', 'jsModuleRuntime'])
    if (!Array.isArray(config.resources[name]) || config.resources[name].length !== 1)
      throw new Error('Exactly one native runtime, native module and runtime module are required.');
  return config;
}

export function executionAssets(config, inventory, loader) {
  if (!Array.isArray(inventory) || inventory.length < 5 || inventory.length > 2048 ||
      new Set(inventory).size !== inventory.length || inventory.some(path =>
        typeof path !== 'string' || !/^(?:_framework|references)\/[A-Za-z0-9_.\/-]+$/.test(path) || path.includes('..')))
    throw new Error('Invalid execution asset inventory.');
  const declared = new Set(inventory), selected = new Set([loader]);
  if (typeof loader !== 'string' || !/^_framework\/dotnet(?:\.[A-Za-z0-9_-]+)?\.js$/.test(loader) || !declared.has(loader))
    throw new Error('The published .NET loader is required for isolation.');
  for (const group of Object.keys(runtimeGroups)) {
    const assets = config.resources[group] ?? [];
    if (!Array.isArray(assets) || assets.length > 1024) throw new Error('Invalid runtime asset group.');
    for (const asset of assets) {
      if (!asset || typeof asset.name !== 'string' || !/^[A-Za-z0-9_.-]+$/.test(asset.name) || asset.name.includes('..'))
        throw new Error('Invalid runtime asset descriptor.');
      const path = '_framework/' + asset.name;
      if (!declared.has(path)) throw new Error('Runtime asset is not in the published inventory: ' + path);
      selected.add(path);
    }
  }
  for (const path of inventory) if (/^references\/(?:[A-Za-z0-9_.-]+\.dll|index\.txt)$/.test(path)) selected.add(path);
  if (!selected.has('references/index.txt')) throw new Error('The compiler metadata manifest is missing.');
  // Satellite resources, service workers and unrelated scripts are not needed by this worker.
  return [...selected];
}

// Narrow only the worker's copy of the published native module. The editor keeps its
// original runtime. WebAssembly engines enforce this maximum on memory.grow, unlike
// supplying wasmMemory to a module that defines (rather than imports) its memory.
export function constrainWasmMemory(buffer, maximumPages = 8192) {
  if (!(buffer instanceof ArrayBuffer) || buffer.byteLength < 8 || buffer.byteLength > 67108864 ||
      !Number.isInteger(maximumPages) || maximumPages < 1 || maximumPages > 8192)
    throw new Error('Invalid isolated Wasm memory configuration.');
  const bytes = new Uint8Array(buffer);
  if ([0,97,115,109,1,0,0,0].some((value,index) => bytes[index] !== value)) throw new Error('Invalid Wasm module header.');
  let cursor = 8, sectionStart = -1, sectionEnd = -1, initial = 0, maximum = 0;
  function u32(end) {
    let result = 0;
    for (let index = 0; index < 5; index++) {
      if (cursor >= end) throw new Error('Truncated Wasm integer.');
      const value = bytes[cursor++];
      if (index === 4 && (value & 240)) throw new Error('Wasm integer overflow.');
      result += (value & 127) * 2 ** (index * 7);
      if (!(value & 128)) return result;
    }
    throw new Error('Invalid Wasm integer.');
  }
  while (cursor < bytes.length) {
    const start = cursor, id = bytes[cursor++], size = u32(bytes.length), end = cursor + size;
    if (end > bytes.length) throw new Error('Truncated Wasm section.');
    if (id === 5) {
      if (sectionStart !== -1 || u32(end) !== 1) throw new Error('Exactly one defined runtime memory is required.');
      const flags = u32(end);
      if (flags !== 0 && flags !== 1) throw new Error('Shared and memory64 runtimes are not supported in this worker.');
      initial = u32(end); const originalMaximum = flags === 1 ? u32(end) : 65536;
      if (initial > originalMaximum || originalMaximum > 65536 || cursor !== end) throw new Error('Invalid Wasm memory limits.');
      maximum = Math.min(maximumPages, originalMaximum);
      if (initial > maximum) throw new Error('Runtime initial memory exceeds the isolation ceiling.');
      sectionStart = start; sectionEnd = end;
    }
    cursor = end;
  }
  if (sectionStart < 0) throw new Error('The runtime must define its own bounded memory.');
  const leb = value => { const result=[]; do { const part=value%128; value=Math.floor(value/128); result.push(part|(value?128:0)); } while(value); return result; };
  const limits = [1,1,...leb(initial),...leb(maximum)], section = [5,...leb(limits.length),...limits];
  const output = new Uint8Array(bytes.length - (sectionEnd-sectionStart) + section.length);
  output.set(bytes.subarray(0,sectionStart)); output.set(section,sectionStart); output.set(bytes.subarray(sectionEnd),sectionStart+section.length);
  return { bytes: output.buffer, initialPages: initial, maximumPages: maximum };
}
