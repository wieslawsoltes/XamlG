// Runs in a dedicated blob worker whose inherited CSP denies every network connection.
// No Blazor/Studio Main method is called. All assets were loaded by the trusted supervisor.
const reply = globalThis.postMessage.bind(globalThis);
const closeWorker = globalThis.close.bind(globalThis);
let ready = false, dispatch = null, busy = false;
const urls = [];
const fail = value => { reply({ type: 'fatal', error: String(value?.message || value).slice(0,4096) }); closeWorker(); };

async function bootstrap(event) {
  const message = event.data;
  if (message?.type !== 'bootstrap') { fail('Worker bootstrap is required.'); return; }
  try {
    if (globalThis.origin !== 'null') throw new Error('Execution requires a physically opaque worker origin.');
    const base = new URL(message.base), files = new Map();
    if (!['https:','http:'].includes(base.protocol) || base.username || base.password || !base.pathname.endsWith('/')) throw new Error('Invalid trusted asset base.');
    let total = 0;
    for (const file of message.files) {
      if (!/^(?:_framework|references)\/[A-Za-z0-9_.\/-]+$/.test(file.name) || file.name.includes('..') || files.has(file.name) || !(file.bytes instanceof ArrayBuffer) ||
          file.bytes.byteLength > 67108864 || (total += file.bytes.byteLength) > 536870912) throw new Error('Invalid worker asset inventory.');
      files.set(file.name, file.bytes);
    }
    const get = path => { const bytes = files.get(path); if (!bytes) throw new Error('Missing isolated runtime asset: ' + path); return bytes; };
    const mime = path => path.endsWith('.wasm') ? 'application/wasm' : path.endsWith('.js') ? 'text/javascript' : 'application/octet-stream';
    const response = path => new Response(get(path).slice(0), { status: 200, headers: { 'content-type': mime(path) } });
    // The .NET HTTP handler can read only the prefetched compiler metadata and runtime assets.
    // CSP independently denies network even if evaluated code discovers another JS API.
    Object.defineProperty(globalThis, 'fetch', { configurable: false, writable: false, value: async input => {
      const url = new URL(typeof input === 'string' ? input : input.url, base);
      if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname) || url.search || url.hash) throw new Error('Network access is not permitted in approved execution.');
      return response(decodeURIComponent(url.pathname.slice(base.pathname.length)));
    } });
    function moduleUrl(path) {
      const url = URL.createObjectURL(new Blob([get(path)], { type: 'text/javascript' })); urls.push(url); return url;
    }
    const original = message.config;
    if (!original || !original.resources) throw new Error('The trusted runtime manifest is missing.');
    const resources = original.resources, assets = [];
    const groups = { wasmNative: 'dotnetwasm', jsModuleNative: 'js-module-native', jsModuleRuntime: 'js-module-runtime',
      coreAssembly: 'assembly', assembly: 'assembly', lazyAssembly: 'assembly', coreVfs: 'vfs', vfs: 'vfs', icu: 'icu' };
    for (const [name,behavior] of Object.entries(groups)) {
      for (const asset of resources[name] || []) {
        if (!asset || typeof asset.name !== 'string' || !/^[A-Za-z0-9_.-]+$/.test(asset.name)) throw new Error('Invalid runtime asset descriptor.');
        const path = '_framework/' + asset.name;
        assets.push({ ...asset, behavior, isCore: behavior === 'assembly', loadRemote: false,
          resolvedUrl: behavior.startsWith('js-module-') ? moduleUrl(path) : new URL(path,base).href,
          buffer: behavior.startsWith('js-module-') ? undefined : get(path) });
      }
    }
    const memory = new WebAssembly.Memory({ initial: 1024, maximum: 8192 });
    const { dotnet } = await import(moduleUrl(message.loader));
    if (typeof dotnet.withModuleConfig !== 'function') throw new Error('This runtime cannot supply the required bounded memory.');
    const runtime = await dotnet.withConfig({ ...original, assets, resources: undefined,
      debugLevel: 0, diagnosticTracing: false, environmentVariables: {}, appsettings: [], extensions: {},
      interpreterPgo: false, loadAllSatelliteResources: false, applicationArguments: [] })
      .withResourceLoader((type,name,uri) => {
        if (type === 'dotnetjs') {
          const found = assets.find(asset => asset.name === name && asset.behavior.startsWith('js-module-'));
          return found?.resolvedUrl;
        }
        const path = '_framework/' + name;
        return Promise.resolve(response(path));
      })
      .withModuleConfig({ wasmMemory: memory })
      .create();
    if (typeof runtime.localHeapViewU8 !== 'function' || runtime.localHeapViewU8().buffer !== memory.buffer)
      throw new Error('The runtime did not honor the isolated 512 MiB Wasm memory ceiling.');
    const exports = await runtime.getAssemblyExports('XamlG.Playground.dll');
    const api = exports.XamlG.Playground.UiCSharpWorkerExports;
    await api.Initialize(base.href);
    dispatch = api.Dispatch;
    // Defense in depth: evaluated code gets no worker spawning or cross-origin messaging APIs.
    for (const name of ['Worker','SharedWorker','BroadcastChannel','WebSocket','EventSource','WebTransport'])
      Object.defineProperty(globalThis,name,{ configurable:false,writable:false,value:class { constructor(){throw new Error('This API is unavailable in approved execution.');} } });
    ready = true;
    addEventListener('message', onCall);
    reply({ type: 'ready', limits: { wasmMemoryBytes: 536870912, compilationMilliseconds: 20000, interactionMilliseconds: 3000, network: false, execution: 'dedicated-worker' } });
  } catch (error) { fail(error); }
}
function onCall(event) {
  const message = event.data;
  if (!ready || busy || message?.type !== 'call' || !Number.isSafeInteger(message.id) || typeof message.json !== 'string' || message.json.length > 1048576) { fail('Invalid isolated execution command.'); return; }
  busy = true;
  try {
    const json = dispatch(message.method,message.json);
    if (typeof json !== 'string' || json.length > 2097152) throw new Error('Isolated result exceeds its bound.');
    reply({ type: 'result', id: message.id, json });
  } catch (error) { reply({ type: 'result', id: message.id, json: '{}', error: String(error?.message || error).slice(0,4096) }); }
  finally { busy = false; }
}
// Remove this listener before constructing the runtime. Some runtimes reserve onmessage during boot.
addEventListener('message', bootstrap, { once: true });
