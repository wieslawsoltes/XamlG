// Physical opaque-origin isolation is required, not merely a cross-origin parent.
export function isIsolated() { return parent !== window && globalThis.origin === 'null'; }

export function connect(dotnet, mode) {
  if (!isIsolated() || !['mcp', 'execution'].includes(mode))
    throw new Error('Opaque-origin sandbox required for intelligent UI guests.');

  let sequence = 0, epoch = 0, disposed = false, ready = false, executionStarted = false;
  let hostOrigin = null, marker = null, queued = null, capabilities = {};
  let delivery = Promise.resolve(), refreshPromise = null, refreshAgain = false, retirement = null;
  const pending = new Map();
  const targetOrigin = () => hostOrigin && hostOrigin !== 'null' ? hostOrigin : '*';
  const post = message => { if (!disposed) parent.postMessage(message, targetOrigin()); };
  const notify = (method, params) => post({ jsonrpc: '2.0', method, params });
  const current = generation => !disposed && generation === epoch;
  const bounded = value => {
    const json = JSON.stringify(value);
    if (typeof json !== 'string' || json.length > 2097152) throw new Error('UI host result must be bounded JSON (at most 2 MiB).');
    return value;
  };
  const validMarker = value => value?.format === 'xamlg.intelligent-ui/1' &&
    typeof value.id === 'string' && /^[A-Za-z0-9_.-]{1,80}$/.test(value.id) &&
    typeof value.sessionId === 'string' && /^[a-f0-9]{32}$/i.test(value.sessionId);

  // Serialize .NET deliveries. Generation checks happen when dequeued, not when
  // enqueued, so a blocked old delivery cannot overwrite a newer host selection.
  function deliver(generation, method, ...args) {
    const operation = delivery.then(() => current(generation) ? dotnet.invokeMethodAsync(method, ...args) : undefined);
    delivery = operation.catch(() => {});
    return operation;
  }
  const fail = (error, generation = epoch) => deliver(generation, 'ReceiveError', String(error?.message ?? error).slice(0, 4096)).catch(() => {});
  function request(method, params) {
    if (disposed || pending.size >= 16) return Promise.reject(new Error('UI request limit or disposed guest.'));
    const id = ++sequence;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pending.delete(id); reject(new Error('UI host request timed out.')); }, 30000);
      pending.set(id, { resolve, reject, timer, method });
      post({ jsonrpc: '2.0', id, method, params });
    });
  }
  async function tool(name, args) {
    if (disposed || mode !== 'mcp' || !ready || !capabilities.serverTools)
      throw new Error('Host-mediated tools are unavailable.');
    const result = bounded(await request('tools/call', { name, arguments: args }));
    if (disposed) throw new Error('UI guest disposed.');
    if (result.isError) throw new Error(result.content?.find(item => item.type === 'text')?.text || 'Tool failed.');
    return result.structuredContent ?? JSON.parse(result.content?.find(item => item.type === 'text')?.text || 'null');
  }
  async function refresh() {
    if (disposed || !marker || !ready) return;
    if (refreshPromise) { refreshAgain = true; return refreshPromise; }
    refreshPromise = (async () => {
      do {
        refreshAgain = false;
        const generation = epoch, selected = marker;
        try {
          const snapshot = await tool('xamlg_ui_read', { id: selected.id });
          if (!current(generation)) continue;
          if (snapshot?.id !== selected.id || snapshot?.sessionId !== selected.sessionId)
            throw new Error('The UI session was released or replaced.');
          await deliver(generation, 'ReceiveSnapshot', snapshot);
        } catch (error) { await fail(error, generation); }
      } while (refreshAgain && !disposed && marker);
    })();
    try { await refreshPromise; } finally { refreshPromise = null; }
  }
  async function receiveResult(result) {
    if (!ready) { queued = result; return; }
    if (result?.isError) { await fail(result.content?.find(item => item.type === 'text')?.text || 'Tool failed.'); return; }
    const value = result?.structuredContent;
    if (!validMarker(value)) return;
    const generation = ++epoch;
    marker = value;
    await deliver(generation, 'BeginSurface', value.id, value.sessionId);
    if (current(generation)) await refresh();
  }
  async function receive(event) {
    if (disposed || event.source !== parent || !isIsolated()) return;
    const message = event.data;
    if (mode === 'execution') {
      if (message?.type !== 'xamlg-ui-execute' || typeof message.nonce !== 'string' || !/^[a-f0-9]{32}$/i.test(message.nonce)) return;
      if (executionStarted) { post({ type: 'xamlg-ui-execution-result', nonce: message.nonce, error: 'Create a fresh frame for new approved source.' }); return; }
      executionStarted = true; hostOrigin = event.origin;
      try {
        const result = await dotnet.invokeMethodAsync('ExecuteApproved', bounded(message.request));
        post({ type: 'xamlg-ui-execution-result', nonce: message.nonce, result });
      } catch (error) { post({ type: 'xamlg-ui-execution-result', nonce: message.nonce, error: String(error.message).slice(0, 4096) }); }
      return;
    }
    if (!message || message.jsonrpc !== '2.0') return;
    const generation = epoch;
    try {
      bounded(message);
      if (message.id !== undefined && !message.method) {
        const call = pending.get(message.id); if (!call) return;
        pending.delete(message.id); clearTimeout(call.timer);
        if (call.method === 'ui/initialize') hostOrigin = event.origin;
        if (message.error) call.reject(new Error(message.error.message || 'Host rejected the request.')); else call.resolve(message.result);
        return;
      }
      if (message.method === 'ui/resource-teardown' && message.id !== undefined) {
        const target = targetOrigin(); await dispose();
        parent.postMessage({ jsonrpc: '2.0', id: message.id, result: {} }, target);
        return;
      }
      if (message.id !== undefined) {
        post({ jsonrpc: '2.0', id: message.id, ...(message.method === 'ping' ? { result: {} } : { error: { code: -32601, message: 'Unsupported UI request.' } }) });
        return;
      }
      if (message.method === 'ui/notifications/tool-result') await receiveResult(message.params);
      else if (message.method === 'notifications/resources/updated' && marker && message.params?.uri === 'xamlg://ui/' + encodeURIComponent(marker.id)) await refresh();
      else if (message.method === 'ui/notifications/host-context-changed' && ready) await deliver(generation, 'HostContextChanged', message.params ?? {});
    } catch (error) { await fail(error, generation); }
  }
  function dispose() {
    if (disposed) return retirement ?? Promise.resolve();
    disposed = true; ready = false; epoch++; marker = null; queued = null; refreshAgain = false;
    removeEventListener('message', receive); removeEventListener('pagehide', dispose); observer.disconnect();
    for (const call of pending.values()) { clearTimeout(call.timer); call.reject(new Error('UI guest disposed.')); }
    pending.clear();
    retirement = delivery.then(() => dotnet.invokeMethodAsync('RetireSurface')).catch(() => {});
    return retirement;
  }

  let lastHeight = 0;
  const observer = new ResizeObserver(() => {
    const height = Math.min(1600, Math.max(240, document.documentElement.scrollHeight));
    if (ready && !disposed && height !== lastHeight) { lastHeight = height; notify('ui/notifications/size-changed', { height }); }
  });
  observer.observe(document.body);
  addEventListener('message', receive); addEventListener('pagehide', dispose, { once: true });
  if (mode === 'mcp') {
    request('ui/initialize', { protocolVersion: '2026-01-26', appInfo: { name: 'XamlG Native Avalonia', version: '1.0.0' }, appCapabilities: {} }).then(async result => {
      if (disposed) return;
      if (result?.protocolVersion !== '2026-01-26') throw new Error('Unsupported MCP Apps protocol version.');
      capabilities = result.hostCapabilities ?? {};
      await deliver(epoch, 'HostContextChanged', result.hostContext ?? {});
      if (disposed) return;
      ready = true; notify('ui/notifications/initialized', {});
      if (queued) { const latest = queued; queued = null; await receiveResult(latest); }
    }).catch(error => fail(error));
  } else { ready = true; post({ type: 'xamlg-ui-execution-ready' }); }

  return {
    tool, refresh, dispose,
    context: state => !disposed && marker && state?.surfaceId === marker.id && state?.sessionId === marker.sessionId && capabilities.updateModelContext
      ? request('ui/update-model-context', { structuredContent: state }) : Promise.resolve(),
    async action(intent) {
      if (disposed || mode !== 'mcp' || !ready) throw new Error('Execution or retired guests have no host-action bridge.');
      if (intent.kind === 'tool') return tool(intent.tool, intent.arguments ?? {});
      if (intent.kind === 'message') {
        if (!capabilities.message) throw new Error('Host messages unavailable.');
        return request('ui/message', { role: 'user', content: [{ type: 'text', text: intent.text }] });
      }
      if (intent.kind === 'openUrl') {
        const url = new URL(intent.text);
        if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) throw new Error('Unsupported URL.');
        if (!capabilities.openLinks) throw new Error('Host links unavailable.');
        return request('ui/open-link', { url: url.href });
      }
      if (intent.kind === 'copy') {
        if (!navigator.clipboard) throw new Error('Clipboard unavailable.');
        return navigator.clipboard.writeText(intent.text);
      }
      throw new Error('Unknown UI action.');
    }
  };
}
