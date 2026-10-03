let port;
let listener;
let busy = false;

export function isIsolated() {
  return parent !== window && globalThis.origin === 'null';
}
export function connect(dotnet) {
  if (!isIsolated()) throw new Error('Opaque-origin sandbox required.');
  listener = event => {
    if (port || event.source !== parent || event.data?.kind !== 'xamlg-preview-connect' ||
        typeof event.data.nonce !== 'string' || event.data.nonce.length !== 64 || event.ports.length !== 1) return;
    const nonce = event.data.nonce;
    port = event.ports[0];
    window.removeEventListener('message', listener);
    port.onmessage = async message => {
      const value = message.data;
      if (!value || !Number.isSafeInteger(value.id) || value.id <= 0) return;
      if (busy) { port.postMessage({ id: value.id, error: 'The isolated runtime is busy.' }); return; }
      busy = true;
      try {
        let result;
        if (value.method === 'execute') {
          const request = value.params;
          if (!request || typeof request.assemblyBase64 !== 'string' || request.assemblyBase64.length > 12 * 1024 * 1024 ||
              typeof request.factoryType !== 'string' || typeof request.populateMethod !== 'string') throw new Error('Invalid execution payload.');
          result = await dotnet.invokeMethodAsync('Execute', request);
        } else if (value.method === 'inspect') result = await dotnet.invokeMethodAsync('Inspect');
        else throw new Error('Unsupported isolated-runtime request.');
        port.postMessage({ id: value.id, result });
      } catch (error) {
        port.postMessage({ id: value.id, error: String(error.message ?? error).slice(0, 16000) });
      } finally { busy = false; }
    };
    port.start();
    port.postMessage({ kind: 'connected', nonce });
  };
  window.addEventListener('message', listener);
  parent.postMessage({ kind: 'xamlg-preview-ready' }, '*');
}
export function disconnect() {
  if (listener) window.removeEventListener('message', listener);
  port?.close(); port = undefined;
}
