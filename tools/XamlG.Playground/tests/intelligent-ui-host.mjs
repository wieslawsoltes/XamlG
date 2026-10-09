import { expect } from '@playwright/test';

/** A real MCP resource hosted in opaque frames. The companion and Avalonia/Wasm
 * application are not mocked; only the embedding host's MCP Apps message relay is supplied. */
export async function mountNativeUi(context, mcp, marker) {
  const resource = await mcp.rpc('resources/read', { uri: 'ui://xamlg/intelligent-ui/native-v1' });
  expect(resource.contents).toHaveLength(1);
  expect(resource.contents[0].mimeType).toBe('text/html;profile=mcp-app');
  const host = await context.newPage(), errors = [];
  host.on('pageerror', error => errors.push(error.message));
  await host.exposeFunction('mcpRequest', (method, params) => mcp.rpc(method, params));
  await host.setContent('<!doctype html><iframe title="Parity native resource" sandbox="allow-scripts" style="width:900px;height:1100px;border:0"></iframe>');
  await host.evaluate(({ html, marker }) => {
    const frame = document.querySelector('iframe');
    const send = message => frame.contentWindow.postMessage(message, '*');
    window.nativeRequests = []; window.nativeContexts = [];
    window.publishNative = value => send({ jsonrpc: '2.0', method: 'ui/notifications/tool-result', params: { structuredContent: value, content: [] } });
    window.updateNative = id => send({ jsonrpc: '2.0', method: 'notifications/resources/updated', params: { uri: 'xamlg://ui/' + encodeURIComponent(id) } });
    addEventListener('message', async event => {
      if (event.source !== frame.contentWindow || event.data?.jsonrpc !== '2.0') return;
      const message = event.data;
      if (message.method === 'ui/initialize') send({ jsonrpc: '2.0', id: message.id, result: { protocolVersion: '2026-01-26', hostInfo: { name: 'parity-acceptance', version: '1' }, hostCapabilities: { serverTools: {}, updateModelContext: {} }, hostContext: { theme: 'light' } } });
      else if (message.method === 'ui/notifications/initialized') window.publishNative(marker);
      else if (message.method === 'tools/call') {
        window.nativeRequests.push(message.params);
        try { send({ jsonrpc: '2.0', id: message.id, result: await window.mcpRequest('tools/call', message.params) }); }
        catch (error) { send({ jsonrpc: '2.0', id: message.id, error: { code: -32603, message: error.message } }); }
      } else if (message.method === 'ui/update-model-context') {
        window.nativeContexts.push(message.params); send({ jsonrpc: '2.0', id: message.id, result: {} });
      } else if (message.id !== undefined && message.method) send({ jsonrpc: '2.0', id: message.id, result: {} });
    });
    frame.srcdoc = html;
  }, { html: resource.contents[0].text, marker });
  const guest = host.frameLocator('iframe[title="Parity native resource"]').frameLocator('iframe[title="Native Avalonia UI"]');
  await expect(guest.getByRole('status')).toContainText('revision ' + marker.revision);
  await expect(guest.locator('canvas.avalonia-canvas')).toBeVisible();
  await guest.locator('details > summary').click();
  return { host, guest, errors };
}

export async function clickNativeTopButton(guest) {
  const input = guest.locator('.avalonia-native-host');
  await expect(input).toBeVisible();
  // Wait for the browser layout/render turn after applying the actual native tree.
  await guest.locator('body').evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await input.click({ position: { x: 80, y: 20 } });
}
