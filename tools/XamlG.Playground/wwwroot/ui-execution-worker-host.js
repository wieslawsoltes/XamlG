// Trusted supervisor in its own opaque origin. CSP is installed BEFORE any worker exists.
// The outer view supplies deployment-owned bytes; this document can make no network requests.
const opaque = globalThis.origin === 'null';
const policy = document.createElement('meta');
policy.httpEquiv = 'Content-Security-Policy';
policy.content = "default-src 'none'; script-src blob: 'unsafe-eval' 'wasm-unsafe-eval'; worker-src blob:; connect-src 'none'; img-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
document.head.append(policy);
let worker = null, channel = null, used = false, active = null, stopped = false;
const send = message => { if (channel && !stopped) channel.postMessage(message); };
function stop(reason) {
  if (stopped) return;
  if (active) { clearTimeout(active.timer); active = null; }
  worker?.terminate(); worker = null;
  send({ type: 'fatal', error: String(reason || 'Execution stopped.').slice(0,4096) });
  stopped = true; channel?.close(); channel = null;
}
function call(message) {
  if (stopped || !worker || active || !Number.isSafeInteger(message.id) || typeof message.method !== 'string' ||
      typeof message.json !== 'string' || message.json.length > 1048576) { stop('Invalid or concurrent worker command.'); return; }
  if (!['publish','read','state','state_action','form_touch','form_submit','form_reset'].includes(message.method)) { stop('Worker command is not permitted.'); return; }
  const deadline = message.method === 'publish' ? 20000 : 3000;
  active = { id: message.id, timer: setTimeout(() => stop('C# execution exceeded its deadline; the worker was terminated.'), deadline) };
  worker.postMessage({ type: 'call', id: message.id, method: message.method, json: message.json });
}
addEventListener('message', event => {
  if (!opaque || used || event.source !== parent || event.data?.type !== 'xamlg-worker-connect' || event.ports.length !== 1) return;
  used = true; channel = event.ports[0]; channel.start();
  channel.onmessage = message => {
    if (stopped) return;
    const value = message.data;
    if (value?.type === 'dispose') { stop('Execution reset.'); return; }
    if (value?.type === 'call') { call(value); return; }
    if (value?.type !== 'bootstrap' || worker || typeof value.source !== 'string' || value.source.length > 131072 || !Array.isArray(value.files) || value.files.length > 2048) { stop('Invalid worker bootstrap.'); return; }
    let bytes = 0;
    for (const file of value.files) {
      if (typeof file.name !== 'string' || !(file.bytes instanceof ArrayBuffer) || (bytes += file.bytes.byteLength) > 536870912) { stop('Execution asset limit exceeded.'); return; }
    }
    const url = URL.createObjectURL(new Blob([value.source], { type: 'text/javascript' }));
    worker = new Worker(url, { type: 'module', name: 'xamlg-approved-csharp' }); URL.revokeObjectURL(url);
    const boot = setTimeout(() => stop('The isolated .NET runtime did not initialize.'), 120000);
    worker.onerror = event => { clearTimeout(boot); event.preventDefault(); stop(event.message || 'Execution worker failed.'); };
    worker.onmessageerror = () => { clearTimeout(boot); stop('Invalid execution worker message.'); };
    worker.onmessage = event => {
      const result = event.data;
      if (result?.type === 'ready') { clearTimeout(boot); send({ type: 'ready', limits: result.limits }); return; }
      if (result?.type === 'fatal') { clearTimeout(boot); stop(result.error); return; }
      if (result?.type !== 'result' || !active || result.id !== active.id || typeof result.json !== 'string' || result.json.length > 2097152) { stop('Invalid execution result.'); return; }
      clearTimeout(active.timer); active = null; send(result);
    };
    worker.postMessage({ type: 'bootstrap', base: value.base, config: value.config, loader: value.loader, files: value.files }, value.files.map(file => file.bytes));
  };
  send({ type: 'connected' });
}, { once: false });
addEventListener('pagehide', () => stop('Execution frame retired.'), { once: true });
parent.postMessage({ type: opaque ? 'xamlg-worker-supervisor-ready' : 'xamlg-worker-supervisor-rejected' }, '*');
