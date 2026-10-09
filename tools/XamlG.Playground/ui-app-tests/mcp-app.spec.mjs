import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
const resource = readFileSync(new URL('../../../src/XamlG.IntelligentUI/Resources/intelligent-ui.html', import.meta.url), 'utf8');

async function mount(page, options = {}) {
  await page.setContent('<iframe title="MCP App" sandbox="allow-scripts" style="width:950px;height:740px;border:0"></iframe>');
  await page.evaluate(({ html, options }) => {
    const frame = document.querySelector('iframe');
    const host = window.uiHost = { calls: [], messages: [], links: [], contexts: [], state: { seats: 8 }, revision: 1, stateRevision: 0, sessionId: 'a'.repeat(32), options };
    const element = (key, type, properties, children = [], extra = {}) => ({ key, type, properties, children, ...extra });
    const snapshot = () => ({ id: 'pricing', sessionId: host.sessionId, revision: host.revision, stateRevision: host.stateRevision, sequence: 1, isFinal: true, xaml: '', state: host.state, data: { unit: 29 }, actions: [], diagnostics: [],
      fallbackMarkdown: '$' + host.state.seats * 29,
      roots: [element('/root', 'StackPanel', { Spacing: 12 }, [
        element('/input', 'Slider', { Minimum: 1, Maximum: 50, Value: host.state.seats }, [], { stateKey: 'seats' }),
        element('/price', 'TextBlock', { Text: '$' + host.state.seats * 29 }),
        element('/untrusted', 'TextBlock', { Text: options.untrusted || 'Example' }),
        element('/action', 'Button', { Content: 'Continue' }, [], { actionId: 'continue' })
      ])] });
    const marker = () => ({ format: 'xamlg.intelligent-ui/1', id: 'pricing', sessionId: 'a'.repeat(32), revision: 1, stateRevision: 0, fallbackMarkdown: 'Pricing fallback' });
    const send = message => frame.contentWindow.postMessage(message, '*');
    host.send = send;
    host.replace = () => { host.sessionId = 'b'.repeat(32); host.revision++; };
    host.mutate = () => { host.revision++; };
    window.addEventListener('message', event => {
      if (event.source !== frame.contentWindow || event.data?.jsonrpc !== '2.0') return;
      const m = event.data;
      const reply = result => send({ jsonrpc: '2.0', id: m.id, result });
      const fail = message => send({ jsonrpc: '2.0', id: m.id, error: { code: -32602, message } });
      if (m.method === 'ui/initialize') {
        host.initialization = m.params;
        reply({ protocolVersion: '2026-01-26', hostInfo: { name: 'deterministic-test', version: '1' }, hostCapabilities: { serverTools: {}, message: {}, openLinks: {}, updateModelContext: {} }, hostContext: { theme: 'light' } });
      } else if (m.method === 'ui/notifications/initialized') {
        send({ jsonrpc: '2.0', method: 'ui/notifications/tool-result', params: { structuredContent: marker(), content: [{ type: 'text', text: 'Pricing fallback' }] } });
      } else if (m.method === 'tools/call') {
        host.calls.push(m.params); const { name, arguments: args } = m.params;
        if (name === 'xamlg_ui_read') { reply({ structuredContent: snapshot(), content: [] }); return; }
        if (args.expectedRevision !== host.revision || args.expectedStateRevision !== host.stateRevision) { fail('revision_conflict'); return; }
        if (name === 'xamlg_ui_state') {
          if (args.key !== 'seats' || !Number.isFinite(args.value) || args.value < 1 || args.value > 50) { fail('invalid_state'); return; }
          host.state.seats = args.value; host.stateRevision++; reply({ structuredContent: snapshot(), content: [] });
        } else if (name === 'xamlg_ui_action') {
          const kind = options.kind || 'message';
          reply({ structuredContent: { surfaceId: 'pricing', revision: host.revision, id: 'continue', kind, text: kind === 'openUrl' ? (options.url || 'https://example.com/') : 'Discuss ' + host.state.seats + ' seats', tool: null, arguments: null }, content: [] });
        } else fail('Unknown tool');
      } else if (m.method === 'ui/message') { host.messages.push(m.params); reply({}); }
      else if (m.method === 'ui/open-link') { host.links.push(m.params); reply({}); }
      else if (m.method === 'ui/update-model-context') { host.contexts.push(m.params); reply({}); }
      else if (m.id !== undefined) reply({});
    });
    frame.srcdoc = html;
  }, { html: resource, options });
  const app = page.frameLocator('iframe[title="MCP App"]');
  await expect(app.getByRole('status')).toContainText('revision 1');
  return app;
}

test('initializes the actual embedded resource and uses parent-mediated structured tool data', async ({ page }) => {
  const app = await mount(page);
  await expect(app.getByText('$232', { exact: true }).first()).toBeVisible();
  const state = await page.evaluate(() => ({ initialization: window.uiHost.initialization, calls: window.uiHost.calls }));
  expect(state.initialization.protocolVersion).toBe('2026-01-26');
  expect(state.calls[0]).toEqual({ name: 'xamlg_ui_read', arguments: { id: 'pricing' } });
});

test('state editing updates computed output without requesting model inference', async ({ page }) => {
  const app = await mount(page);
  await app.getByRole('slider').evaluate(input => { input.value = '10'; input.dispatchEvent(new Event('change', { bubbles: true })); });
  await expect(app.getByRole('status')).toContainText('state 1');
  await expect(app.getByText('$290', { exact: true }).first()).toBeVisible();
  expect(await page.evaluate(() => window.uiHost.calls.filter(call => call.name === 'xamlg_ui_state'))).toHaveLength(1);
  expect(await page.evaluate(() => window.uiHost.messages)).toEqual([]);
  await expect.poll(() => page.evaluate(() => window.uiHost.contexts.length)).toBe(1);
});

test('actions require review and use the MCP Apps content array', async ({ page }) => {
  const app = await mount(page);
  await app.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(app.getByRole('region', { name: 'Review UI action' })).toBeVisible();
  expect(await page.evaluate(() => window.uiHost.messages)).toEqual([]);
  await app.getByRole('button', { name: 'Confirm', exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.uiHost.messages.length)).toBe(1);
  expect(await page.evaluate(() => window.uiHost.messages[0])).toEqual({ role: 'user', content: [{ type: 'text', text: 'Discuss 8 seats' }] });
});

test('a stale review cannot send a message', async ({ page }) => {
  const app = await mount(page);
  await app.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(app.getByRole('region', { name: 'Review UI action' })).toBeVisible();
  await page.evaluate(() => window.uiHost.mutate());
  await app.getByRole('button', { name: 'Confirm', exact: true }).click();
  await expect(app.getByRole('alert')).toContainText('revision_conflict');
  expect(await page.evaluate(() => window.uiHost.messages)).toEqual([]);
});

test('released IDs cannot attach an old card to a different owner session', async ({ page }) => {
  const app = await mount(page);
  await page.evaluate(() => window.uiHost.replace());
  await app.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(app.getByRole('alert')).toContainText('released or replaced');
  await expect(app.getByRole('slider')).toHaveCount(0);
});

test('untrusted strings remain text and script links are rejected', async ({ page }) => {
  const malicious = '<img src="https://invalid.test/x" onerror="parent.pwned=true">';
  const app = await mount(page, { untrusted: malicious, kind: 'openUrl', url: 'javascript:alert(1)' });
  await expect(app.getByText(malicious, { exact: true })).toBeVisible();
  await expect(app.locator('img')).toHaveCount(0);
  await app.getByRole('button', { name: 'Continue', exact: true }).click();
  await app.getByRole('button', { name: 'Confirm', exact: true }).click();
  await expect(app.getByRole('alert')).toContainText('Unsupported URL');
  expect(await page.evaluate(() => window.uiHost.links)).toEqual([]);
});

test('messages from the app window cannot spoof the parent transport', async ({ page }) => {
  const app = await mount(page);
  await app.locator('body').evaluate(() => window.postMessage({ jsonrpc: '2.0', method: 'ui/notifications/tool-result', params: { structuredContent: { format: 'xamlg.intelligent-ui/1', id: 'foreign', sessionId: 'c'.repeat(32), fallbackMarkdown: 'spoofed' } } }, '*'));
  await expect(app.getByRole('status')).toContainText('revision 1');
  await expect(app.getByText('spoofed', { exact: true })).toHaveCount(0);
});
