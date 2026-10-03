const clients = new Map();
let nextHandle = 0;

// Ownership is established synchronously. The caller can discard the iframe even
// before the second runtime downloads, starts .NET, or sends its ready message.
export function create(host, url) {
  const id = ++nextHandle;
  const iframe = document.createElement('iframe');
  iframe.setAttribute('sandbox', 'allow-scripts');
  iframe.setAttribute('referrerpolicy', 'no-referrer');
  iframe.setAttribute('title', 'XamlG isolated preview');
  iframe.setAttribute('allow', "camera 'none'; microphone 'none'; geolocation 'none'; clipboard-read 'none'; clipboard-write 'none'; payment 'none'; usb 'none'");
  iframe.style.cssText = 'border:0;width:100%;height:100%;display:block;background:white';
  const channel = new MessageChannel();
  const nonce = Array.from(crypto.getRandomValues(new Uint8Array(32)), value => value.toString(16).padStart(2, '0')).join('');
  let resolveReady;
  let rejectReady;
  const readyPromise = new Promise((resolve, reject) => { resolveReady = resolve; rejectReady = reject; });
  // Reset can happen before any request observes readiness. Observe that rejection
  // without changing the promise awaited by callers.
  void readyPromise.catch(() => {});
  const client = { iframe, port: channel.port1, pending: new Map(), sequence: 0,
    state: 'starting', requests: 0, ready: readyPromise, dispose: null };
  let transferred = false;
  let timeout;
  const cleanupHandshake = () => {
    clearTimeout(timeout);
    window.removeEventListener('message', ready);
  };
  const close = error => {
    if (client.state === 'closed') return;
    client.state = 'closed';
    cleanupHandshake();
    rejectReady(error);
    for (const pending of client.pending.values()) {
      clearTimeout(pending.timeout);
      pending.reject(error);
    }
    client.pending.clear();
    client.port.onmessage = null;
    client.port.close();
    channel.port2.close();
    iframe.remove();
    clients.delete(id);
  };
  const ready = event => {
    if (client.state !== 'starting' || transferred || event.source !== iframe.contentWindow ||
        event.origin !== 'null' || event.data?.kind !== 'xamlg-preview-ready') return;
    try {
      transferred = true;
      iframe.contentWindow.postMessage({ kind: 'xamlg-preview-connect', nonce }, '*', [channel.port2]);
    } catch (error) { close(error); }
  };
  client.dispose = () => close(new Error('The isolated execution frame was reset.'));
  client.port.onmessage = event => {
    const message = event.data;
    if (client.state === 'starting') {
      if (message?.kind === 'connected' && message.nonce === nonce) {
        client.state = 'ready';
        cleanupHandshake();
        resolveReady();
      }
      return;
    }
    if (client.state !== 'ready' || !message || !Number.isSafeInteger(message.id)) return;
    const pending = client.pending.get(message.id);
    if (!pending) return;
    client.pending.delete(message.id);
    clearTimeout(pending.timeout);
    if (typeof message.error === 'string') pending.reject(new Error(message.error.slice(0, 16000)));
    else {
      try {
        if (!message.result || JSON.stringify(message.result).length > 16 * 1024 * 1024)
          throw new Error('Invalid or oversized preview response.');
        pending.resolve(message.result);
      } catch (error) { pending.reject(error); }
    }
  };
  clients.set(id, client);
  try {
    window.addEventListener('message', ready);
    timeout = setTimeout(() => close(new Error('Isolated runtime initialization timed out. Reset the preview and retry.')), 90000);
    client.port.start();
    iframe.src = url;
    host.replaceChildren(iframe);
  } catch (error) {
    close(error);
    throw error;
  }
  return id;
}

export async function request(id, method, params) {
  const client = clients.get(id);
  if (!client || !['execute', 'inspect'].includes(method)) throw new Error('Invalid isolated-runtime request.');
  // Count requests waiting for startup too, not just messages already sent.
  if (client.requests >= 4) throw new Error('Too many pending isolated-runtime requests.');
  client.requests++;
  try {
    await client.ready;
    if (client.state !== 'ready' || clients.get(id) !== client)
      throw new Error('The isolated execution frame was reset.');
    const sequence = ++client.sequence;
    return await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        client.pending.delete(sequence);
        reject(new Error('The isolated code did not respond. Reset the preview to discard its runtime. Browser sandboxing is not an operating-system execution quota.'));
      }, 30000);
      client.pending.set(sequence, { resolve, reject, timeout });
      try { client.port.postMessage({ id: sequence, method, params }); }
      catch (error) {
        clearTimeout(timeout);
        client.pending.delete(sequence);
        reject(error);
      }
    });
  } finally { client.requests--; }
}

export function dispose(id) { clients.get(id)?.dispose(); }
