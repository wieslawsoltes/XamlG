import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../wwwroot/ui-native-guest.js', import.meta.url), 'utf8');

async function mount(page, earlyInvalid = false) {
  await page.setContent('<iframe title="Native bridge" sandbox="allow-scripts" style="width:700px;height:400px"></iframe>');
  await page.evaluate(({ source, earlyInvalid }) => {
    const iframe = document.querySelector('iframe');
    const host = window.nativeHost = { calls: [], contexts: [], responses: [], held: [], hold: false, initialized: false };
    const session = id => id.repeat(32);
    const send = message => iframe.contentWindow.postMessage(message, '*');
    host.send = send;
    host.publish = id => send({ jsonrpc: '2.0', method: 'ui/notifications/tool-result', params: { content: [], structuredContent: { format: 'xamlg.intelligent-ui/1', id, sessionId: session(id) } } });
    host.flush = () => { host.hold = false; for (const action of host.held.splice(0)) action(); };
    addEventListener('message', event => {
      if (event.source !== iframe.contentWindow || event.data?.jsonrpc !== '2.0') return;
      const message = event.data, reply = result => send({ jsonrpc: '2.0', id: message.id, result });
      if (!message.method) { host.responses.push(message); return; }
      if (message.method === 'ui/initialize') {
        if (earlyInvalid) send({ jsonrpc: '2.0', method: 'ui/notifications/tool-result', params: { structuredContent: { format: 'xamlg.intelligent-ui/1', id: '../bad', sessionId: 'bad' } } });
        reply({ protocolVersion: '2026-01-26', hostCapabilities: { serverTools: {}, updateModelContext: {} }, hostContext: { theme: 'light' } });
      } else if (message.method === 'ui/notifications/initialized') {
        host.initialized = true; if (!earlyInvalid) host.publish('a');
      } else if (message.method === 'tools/call') {
        host.calls.push(message.params);
        const id = message.params.arguments.id;
        const deliver = () => reply({ content: [], structuredContent: { id, sessionId: session(id), revision: 1, stateRevision: 0 } });
        if (host.hold) host.held.push(deliver); else deliver();
      } else if (message.method === 'ui/update-model-context') { host.contexts.push(message.params); reply({}); }
      else if (message.id !== undefined) reply({});
    });
    iframe.srcdoc = '<!doctype html><main role="status">Waiting</main><script type="module">' + source + '\n' + `
      const probe = window.nativeProbe = { deliveries: [], errors: [] };
      const dotnet = { async invokeMethodAsync(method, ...args) {
        probe.deliveries.push({ method, args });
        if (method === 'BeginSurface') document.querySelector('main').textContent = 'Loading ' + args[0];
        else if (method === 'ReceiveSnapshot') document.querySelector('main').textContent = 'Rendered ' + args[0].id;
        else if (method === 'RetireSurface') document.querySelector('main').textContent = 'Retired';
        else if (method === 'ReceiveError') probe.errors.push(args[0]);
      }};
      window.nativeBridge = connect(dotnet, 'mcp');
    ` + '</script>';
  }, { source, earlyInvalid });
  const frame = page.frameLocator('iframe[title="Native bridge"]');
  await expect.poll(() => page.evaluate(() => window.nativeHost.initialized)).toBe(true);
  if (!earlyInvalid) await expect(frame.getByRole('status')).toHaveText('Rendered a');
  return frame;
}

async function refreshWithoutWaiting(frame, count = 1) {
  await frame.locator('body').evaluate((_, count) => {
    for (let i = 0; i < count; i++) void window.nativeBridge.refresh().catch(error => window.nativeProbe.errors.push(error.message));
  }, count);
}

test('native guest validates queued pre-initialization markers identically to live markers', async ({ page }) => {
  const frame = await mount(page, true);
  expect(await page.evaluate(() => window.nativeHost.calls)).toEqual([]);
  await page.evaluate(() => window.nativeHost.publish('b'));
  await expect(frame.getByRole('status')).toHaveText('Rendered b');
  expect(await page.evaluate(() => window.nativeHost.calls)).toEqual([{ name: 'xamlg_ui_read', arguments: { id: 'b' } }]);
});

test('native guest discards stale reads when the selected surface changes', async ({ page }) => {
  const frame = await mount(page);
  await page.evaluate(() => { window.nativeHost.hold = true; });
  await refreshWithoutWaiting(frame);
  await expect.poll(() => page.evaluate(() => window.nativeHost.held.length)).toBe(1);
  await page.evaluate(() => window.nativeHost.publish('b'));
  await expect(frame.getByRole('status')).toHaveText('Loading b');
  await page.evaluate(() => window.nativeHost.flush());
  await expect(frame.getByRole('status')).toHaveText('Rendered b');
  expect(await frame.locator('body').evaluate(() => window.nativeProbe.deliveries.filter(call => call.method === 'ReceiveSnapshot').map(call => call.args[0].id))).toEqual(['a', 'b']);
  expect(await frame.locator('body').evaluate(() => window.nativeProbe.errors)).toEqual([]);
});

test('native refresh notifications coalesce into one current read and one follow-up', async ({ page }) => {
  const frame = await mount(page);
  await page.evaluate(() => { window.nativeHost.hold = true; });
  await refreshWithoutWaiting(frame, 4);
  await expect.poll(() => page.evaluate(() => window.nativeHost.held.length)).toBe(1);
  expect(await page.evaluate(() => window.nativeHost.calls.length)).toBe(2);
  await page.evaluate(() => window.nativeHost.flush());
  await expect.poll(() => page.evaluate(() => window.nativeHost.calls.length)).toBe(3);
  expect(await frame.locator('body').evaluate(() => window.nativeProbe.errors)).toEqual([]);
});

test('native teardown retires the view before acknowledgement and cannot resurrect from pending reads', async ({ page }) => {
  const frame = await mount(page);
  await page.evaluate(() => { window.nativeHost.hold = true; });
  await refreshWithoutWaiting(frame);
  await expect.poll(() => page.evaluate(() => window.nativeHost.held.length)).toBe(1);
  await page.evaluate(() => window.nativeHost.send({ jsonrpc: '2.0', method: 'ui/resource-teardown', id: 'close', params: {} }));
  await expect.poll(() => page.evaluate(() => window.nativeHost.responses.some(response => response.id === 'close'))).toBe(true);
  await expect(frame.getByRole('status')).toHaveText('Retired');
  await page.evaluate(() => window.nativeHost.flush());
  await frame.locator('body').evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await expect(frame.getByRole('status')).toHaveText('Retired');
  expect(await frame.locator('body').evaluate(() => window.nativeProbe.deliveries.filter(call => call.method === 'ReceiveSnapshot').length)).toBe(1);
  expect(await frame.locator('body').evaluate(() => window.nativeProbe.errors)).toEqual([]);
});

test('native guest handles ping and rejects unknown host requests without hanging', async ({ page }) => {
  await mount(page);
  await page.evaluate(() => {
    window.nativeHost.send({ jsonrpc: '2.0', method: 'ping', id: 'ping', params: {} });
    window.nativeHost.send({ jsonrpc: '2.0', method: 'unsupported', id: 'unknown', params: {} });
  });
  await expect.poll(() => page.evaluate(() => window.nativeHost.responses.length)).toBe(2);
  expect(await page.evaluate(() => window.nativeHost.responses)).toEqual([
    { jsonrpc: '2.0', id: 'ping', result: {} },
    { jsonrpc: '2.0', id: 'unknown', error: { code: -32601, message: 'Unsupported UI request.' } }
  ]);
});

test('native guest does not publish context for another surface session', async ({ page }) => {
  const frame = await mount(page);
  await frame.locator('body').evaluate(async () => {
    await window.nativeBridge.context({ surfaceId: 'b', sessionId: 'b'.repeat(32), state: {} });
    await window.nativeBridge.context({ surfaceId: 'a', sessionId: 'b'.repeat(32), state: {} });
    await window.nativeBridge.context({ surfaceId: 'a', sessionId: 'a'.repeat(32), state: { count: 1 } });
  });
  expect(await page.evaluate(() => window.nativeHost.contexts)).toEqual([{ structuredContent: { surfaceId: 'a', sessionId: 'a'.repeat(32), state: { count: 1 } } }]);
});
