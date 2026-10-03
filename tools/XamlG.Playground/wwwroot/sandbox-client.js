const clients = new Map();
let nextHandle = 0;

export async function create(host, url) {
  const id = ++nextHandle;
  const iframe = document.createElement('iframe');
  iframe.setAttribute('sandbox', 'allow-scripts');
  iframe.setAttribute('referrerpolicy', 'no-referrer');
  iframe.setAttribute('title', 'XamlG isolated preview');
  iframe.setAttribute('allow', "camera 'none'; microphone 'none'; geolocation 'none'; clipboard-read 'none'; clipboard-write 'none'; payment 'none'; usb 'none'");
  iframe.style.cssText = 'border:0;width:100%;height:100%;display:block;background:white';
  const channel = new MessageChannel();
  const nonce = Array.from(crypto.getRandomValues(new Uint8Array(32)), value => value.toString(16).padStart(2, '0')).join('');
  const client = { iframe, port: channel.port1, pending: new Map(), sequence: 0, dispose: null };
  clients.set(id, client);
  await new Promise((resolve, reject) => {
    let transferred = false;
    const timeout = setTimeout(() => fail(new Error('Isolated runtime initialization timed out. Reset the preview and retry.')), 90000);
    const cleanup = () => { clearTimeout(timeout); window.removeEventListener('message', ready); };
    const fail = error => { cleanup(); client.port.close(); channel.port2.close(); iframe.remove(); clients.delete(id); reject(error); };
    const ready = event => {
      if (transferred || event.source !== iframe.contentWindow || event.origin !== 'null' || event.data?.kind !== 'xamlg-preview-ready') return;
      transferred = true;
      iframe.contentWindow.postMessage({ kind: 'xamlg-preview-connect', nonce }, '*', [channel.port2]);
    };
    window.addEventListener('message', ready);
    client.port.onmessage = event => {
      const message = event.data;
      if (message?.kind === 'connected' && message.nonce === nonce) {
        cleanup(); resolve(); return;
      }
      if (!message || !Number.isSafeInteger(message.id)) return;
      const pending = client.pending.get(message.id);
      if (!pending) return;
      client.pending.delete(message.id); clearTimeout(pending.timeout);
      if (typeof message.error === 'string') pending.reject(new Error(message.error));
      else {
        try {
          if (!message.result || JSON.stringify(message.result).length > 16 * 1024 * 1024) throw new Error('Invalid or oversized preview response.');
          pending.resolve(message.result);
        } catch (error) { pending.reject(error); }
      }
    };
    client.port.start();
    client.dispose = () => {
      cleanup();
      for (const pending of client.pending.values()) { clearTimeout(pending.timeout); pending.reject(new Error('The isolated execution frame was reset.')); }
      client.pending.clear(); client.port.close(); iframe.remove();
    };
    iframe.src = url;
    host.replaceChildren(iframe);
  });
  return id;
}
export function request(id, method, params) {
  const client = clients.get(id);
  if (!client || !['execute', 'inspect'].includes(method)) throw new Error('Invalid isolated-runtime request.');
  if (client.pending.size >= 4) throw new Error('Too many pending isolated-runtime requests.');
  const sequence = ++client.sequence;
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      client.pending.delete(sequence);
      reject(new Error('The isolated code did not respond. Reset the preview to discard its runtime. Browser sandboxing is not an operating-system execution quota.'));
    }, 30000);
    client.pending.set(sequence, { resolve, reject, timeout });
    client.port.postMessage({ id: sequence, method, params });
  });
}
export function dispose(id) {
  const client = clients.get(id);
  client?.dispose?.(); clients.delete(id);
}
