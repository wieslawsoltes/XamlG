import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
const html = readFileSync(new URL('../../../src/XamlG.IntelligentUI/Resources/intelligent-ui.html', import.meta.url), 'utf8');

async function mount(page) {
  await page.setContent('<iframe title="Parity app" sandbox="allow-scripts" style="width:700px;height:720px;border:0"></iframe>');
  await page.evaluate(html => {
    const frame = document.querySelector('iframe');
    const host = window.parityHost = { id: 'a', count: 0, revision: 1, stateRevision: 0, calls: [], contexts: [], external: [], held: [], hold: false, reject: false, width: 200 };
    const node = (key, type, properties, children = [], extra = {}) => ({ key, type, properties, children, ...extra });
    const session = id => id.repeat(32);
    const snapshot = id => ({ id, sessionId: session(id), revision: host.revision, stateRevision: host.stateRevision, isFinal: true, sequence: 1,
      state: { count: host.count }, data: {}, diagnostics: [], actions: [{ id: 'increment', kind: 'state', arguments: { count: '{ui:Expr state.count + 1}' } }],
      fallbackMarkdown: id + ': ' + host.count,
      roots: [node('/root', 'StackPanel', { Spacing: 4 }, [
        node('/count', 'TextBlock', { Text: id + ': ' + host.count }),
        node('/increment', 'Button', { Content: 'Increment' }, [], { actionId: 'increment' }),
        node('/scale', 'Viewbox', { Width: host.width, Stretch: 'Uniform', StretchDirection: 'DownOnly' }, [
          node('/plot', 'Canvas', { Width: 800, Height: 400, 'AutomationProperties.Name': 'Plot' }, [
            node('/bar', 'Rectangle', { Width: 200, Height: 400, Fill: 'Teal', 'AutomationProperties.Name': 'Bar' })
          ])
        ]),
        node('/code', 'ScrollViewer', { MaxHeight: 100, 'AutomationProperties.Name': 'Code viewport' }, [
          node('/source', 'SelectableTextBlock', { Text: 'line\n'.repeat(50), TextWrapping: 'NoWrap' })
        ])
      ])] });
    const send = message => frame.contentWindow.postMessage(message, '*');
    host.send = send;
    host.publish = id => { host.id = id; send({ jsonrpc: '2.0', method: 'ui/notifications/tool-result', params: { structuredContent: { format: 'xamlg.intelligent-ui/1', id, sessionId: session(id), fallbackMarkdown: id }, content: [] } }); };
    host.flush = () => { host.hold = false; for (const reply of host.held.splice(0)) reply(); };
    addEventListener('message', event => {
      if (event.source !== frame.contentWindow || event.data?.jsonrpc !== '2.0') return;
      const m = event.data, reply = result => send({ jsonrpc: '2.0', id: m.id, result });
      if (m.method === 'ui/initialize') reply({ protocolVersion: '2026-01-26', hostCapabilities: { serverTools: {}, updateModelContext: {}, message: {}, openLinks: {} }, hostContext: { theme: 'light' } });
      else if (m.method === 'ui/notifications/initialized') host.publish('a');
      else if (m.method === 'tools/call') {
        host.calls.push(m.params); const { name, arguments: args } = m.params;
        if (name === 'xamlg_ui_read') {
          const captured = snapshot(args.id), deliver = () => reply({ structuredContent: captured, content: [] });
          if (host.hold) host.held.push(deliver); else deliver();
        } else if (name === 'xamlg_ui_state_action') {
          if (host.reject || args.id !== host.id || args.expectedRevision !== host.revision || args.expectedStateRevision !== host.stateRevision)
            reply({ isError: true, content: [{ type: 'text', text: 'revision_conflict' }] });
          else { host.count++; host.stateRevision++; reply({ structuredContent: snapshot(host.id), content: [] }); }
        } else { host.external.push(m); reply({ isError: true, content: [{ type: 'text', text: 'Unexpected tool' }] }); }
      } else if (m.method === 'ui/update-model-context') { host.contexts.push(m.params); reply({}); }
      else if (m.method === 'ui/message' || m.method === 'ui/open-link') { host.external.push(m); reply({}); }
      else if (m.id !== undefined) reply({});
    });
    frame.srcdoc = html;
  }, html);
  const app = page.frameLocator('iframe[title="Parity app"]');
  await expect(app.getByRole('status')).toContainText('revision 1');
  return app;
}

test('state buttons execute once without external action review or inference', async ({ page }) => {
  const app = await mount(page), button = app.getByRole('button', { name: 'Increment', exact: true });
  await button.click(); await expect(app.getByRole('status')).toContainText('state 1');
  await expect.poll(() => page.evaluate(() => window.parityHost.contexts.length)).toBe(1);
  await button.click(); await expect(app.getByRole('status')).toContainText('state 2');
  await expect(app.getByRole('region', { name: 'Review UI action' })).toBeHidden();
  const calls = await page.evaluate(() => window.parityHost.calls.filter(call => call.name !== 'xamlg_ui_read'));
  expect(calls).toEqual([0, 1].map(expectedStateRevision => ({ name: 'xamlg_ui_state_action', arguments: { id: 'a', expectedRevision: 1, expectedStateRevision, nodeKey: '/increment' } })));
  expect(await page.evaluate(() => window.parityHost.external)).toEqual([]);
});

test('rejected state actions preserve the committed view and allow retry', async ({ page }) => {
  const app = await mount(page);
  await page.evaluate(() => { window.parityHost.reject = true; });
  await app.getByRole('button', { name: 'Increment', exact: true }).click();
  await expect(app.getByRole('alert')).toContainText('revision_conflict');
  await expect(app.getByRole('status')).toContainText('state 0');
  await page.evaluate(() => { window.parityHost.reject = false; });
  await app.getByRole('button', { name: 'Increment', exact: true }).click();
  await expect(app.getByRole('status')).toContainText('state 1');
  await expect(app.getByRole('alert')).toBeEmpty();
});

test('a late snapshot for the previous surface cannot replace the new card', async ({ page }) => {
  const app = await mount(page);
  await page.evaluate(() => { window.parityHost.hold = true; });
  await app.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.parityHost.held.length)).toBe(1);
  await page.evaluate(() => window.parityHost.publish('b'));
  await expect(app.getByRole('button', { name: 'Increment', exact: true })).toHaveCount(0);
  await page.evaluate(() => window.parityHost.flush());
  await expect(app.getByText('b: 0', { exact: true }).first()).toBeVisible();
  await expect(app.getByText('a: 0', { exact: true })).toHaveCount(0);
  await expect(app.getByRole('alert')).toBeEmpty();
});

test('portable Viewbox scales chart geometry and respects down-only stretch', async ({ page }) => {
  const app = await mount(page);
  await expect.poll(async () => (await app.getByLabel('Plot', { exact: true }).boundingBox())?.width).toBe(200);
  expect((await app.getByLabel('Plot', { exact: true }).boundingBox()).height).toBe(100);
  expect((await app.getByLabel('Bar', { exact: true }).boundingBox()).width).toBe(50);
  await page.evaluate(() => { window.parityHost.width = 1000; window.parityHost.revision++; window.parityHost.publish('a'); });
  await expect(app.getByRole('status')).toContainText('revision 2');
  await expect.poll(async () => (await app.getByLabel('Plot', { exact: true }).boundingBox())?.width).toBe(800);
});

test('portable code viewport honors declared MaxHeight', async ({ page }) => {
  const app = await mount(page);
  await expect(app.getByLabel('Code viewport')).toHaveCSS('max-height', '100px');
  expect((await app.getByLabel('Code viewport').boundingBox()).height).toBeLessThanOrEqual(100);
});

test('teardown invalidates pending reads and prevents UI resurrection', async ({ page }) => {
  const app = await mount(page);
  await page.evaluate(() => { window.parityHost.hold = true; });
  await app.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.parityHost.held.length)).toBe(1);
  await page.evaluate(() => window.parityHost.send({ jsonrpc: '2.0', id: 'teardown', method: 'ui/resource-teardown', params: {} }));
  await expect(app.getByRole('main')).toBeEmpty();
  await page.evaluate(() => window.parityHost.flush());
  await app.locator('body').evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await expect(app.getByRole('main')).toBeEmpty();
});
