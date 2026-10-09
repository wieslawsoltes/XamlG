import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
const html = readFileSync(new URL('../../../src/XamlG.IntelligentUI/Resources/intelligent-ui.html', import.meta.url), 'utf8');

async function mount(page, asynchronous = false) {
  await page.setContent('<iframe title="Form app" sandbox="allow-scripts" style="width:600px;height:650px"></iframe>');
  await page.evaluate(({ html, asynchronous }) => {
    const frame = document.querySelector('iframe');
    const host = window.formHost = { name: '', touched: false, submitted: false, pending: false, validated: false, stateRevision: 0, calls: [], messages: [], checks: 0, lastValidation: null };
    const send = message => frame.contentWindow.postMessage(message, '*');
    const role = (name, extra = {}) => ({ id: '/form', role: name, errorMode: 'OnTouch', showErrors: true, authorEnabled: true, ...extra });
    const node = (key, type, properties, children = [], extra = {}) => ({ key, type, properties, children, ...extra });
    const valid = () => !!host.name.trim() && !host.pending && (!asynchronous || host.validated);
    const snapshot = () => {
      const message = host.name.trim() ? null : 'Name is required.';
      const shown = !!message && (host.touched || host.submitted);
      return { id: 'form', sessionId: 'a'.repeat(32), revision: 1, stateRevision: host.stateRevision, sequence: 1, isFinal: true,
        state: { name: host.name }, data: {}, diagnostics: [], actions: [{ id: 'send', kind: 'message', text: 'Send ' + host.name }],
        fallbackMarkdown: shown ? message : host.pending ? 'Validating…' : host.name,
        roots: [node('/form', 'StackPanel', { Spacing: 8 }, [
          node('/name', 'TextBox', { Text: host.name, 'AutomationProperties.Name': 'Name' }, [], { stateKey: 'name', form: role('input') }),
          node('/error', 'TextBlock', { Text: message || '', IsVisible: shown }),
          node('/send', 'Button', { Content: 'Send', IsEnabled: !!host.name.trim() && !host.pending }, [], { actionId: 'send', form: role('submit') })
        ], { form: role('form'), formState: { id: '/form', stamp: host.name, pending: host.pending, validated: host.validated, isValid: valid(), submitted: host.submitted,
          fields: [{ key: '/name', stateKey: 'name', initialValue: '', value: host.name, touched: host.touched, dirty: !!host.name, error: message }] } })] };
    };
    host.complete = () => {
      if (host.pending && host.lastValidation === host.name) { host.pending = false; host.validated = true; host.stateRevision++; }
    };
    host.teardown = () => send({ jsonrpc: '2.0', id: 900, method: 'ui/resource-teardown', params: {} });
    addEventListener('message', async event => {
      if (event.source !== frame.contentWindow || event.data?.jsonrpc !== '2.0') return;
      const m = event.data, reply = result => send({ jsonrpc: '2.0', id: m.id, result });
      if (m.method === 'ui/initialize') reply({ protocolVersion: '2026-01-26', hostCapabilities: { serverTools: {}, updateModelContext: {}, message: {} } });
      else if (m.method === 'ui/notifications/initialized') send({ jsonrpc: '2.0', method: 'ui/notifications/tool-result', params: { structuredContent: { format: 'xamlg.intelligent-ui/1', id: 'form', sessionId: 'a'.repeat(32) }, content: [] } });
      else if (m.method === 'tools/call') {
        const { name, arguments: args } = m.params; host.calls.push(m.params);
        if (name === 'xamlg_ui_read') { reply({ structuredContent: snapshot(), content: [] }); return; }
        if (args.expectedRevision !== 1 || args.expectedStateRevision !== host.stateRevision) { reply({ isError: true, content: [{ type: 'text', text: 'revision_conflict' }] }); return; }
        if (name === 'xamlg_ui_state') { await new Promise(resolve => setTimeout(resolve, 20)); host.name = args.value; host.pending = false; host.validated = false; host.stateRevision++; }
        else if (name === 'xamlg_ui_form_touch') { host.touched = true; host.stateRevision++; }
        else if (name === 'xamlg_ui_form_submit') {
          if (!host.submitted) { host.submitted = true; host.stateRevision++; }
          reply({ structuredContent: { snapshot: snapshot(), action: valid() ? { id: 'form', expectedRevision: 1, expectedStateRevision: host.stateRevision, nodeKey: '/send' } : null,
            focusKey: host.name.trim() ? null : '/name', requiresValidation: asynchronous && !!host.name.trim() && !host.validated && !host.pending }, content: [] }); return;
        } else if (name === 'xamlg_ui_form_validate_start') { host.pending = true; host.validated = false; host.lastValidation = host.name; host.checks++; host.stateRevision++; }
        else if (name === 'xamlg_ui_action') {
          if (!valid()) { reply({ isError: true, content: [{ type: 'text', text: 'invalid_form' }] }); return; }
          reply({ structuredContent: { surfaceId: 'form', revision: 1, id: 'send', kind: 'message', text: 'Send ' + host.name, tool: null, arguments: null }, content: [] }); return;
        } else { reply({ isError: true, content: [{ type: 'text', text: 'Unexpected tool ' + name }] }); return; }
        reply({ structuredContent: snapshot(), content: [] });
      } else if (m.method === 'ui/message') { host.messages.push(m.params); reply({}); }
      else if (m.id !== undefined && m.method) reply({});
    });
    frame.srcdoc = html;
  }, { html, asynchronous });
  const app = page.frameLocator('iframe[title="Form app"]');
  await expect(app.getByRole('status')).toContainText('revision 1');
  return app;
}

test('Enter reveals errors and focuses the first invalid input without executing an action', async ({ page }) => {
  const app = await mount(page), name = app.getByRole('textbox', { name: 'Name', exact: true });
  await expect(app.locator('[data-ui-key="/error"]')).toBeHidden();
  await name.focus(); await name.press('Enter');
  await expect(app.locator('[data-ui-key="/error"]')).toHaveText('Name is required.');
  await expect(name).toBeFocused(); await expect(name).toHaveAttribute('aria-invalid', 'true');
  expect(await page.evaluate(() => window.formHost.messages)).toEqual([]);
  expect(await page.evaluate(() => window.formHost.calls.filter(call => call.name === 'xamlg_ui_action'))).toEqual([]);
});

test('blur queues after value commit and Enter opens the ordinary explicit action review', async ({ page }) => {
  const app = await mount(page), name = app.getByRole('textbox', { name: 'Name', exact: true });
  await name.fill('Ada'); await name.press('Tab');
  await expect.poll(() => page.evaluate(() => window.formHost.touched)).toBe(true);
  const changes = await page.evaluate(() => window.formHost.calls.filter(call => ['xamlg_ui_state', 'xamlg_ui_form_touch'].includes(call.name)));
  expect(changes.map(call => [call.name, call.arguments.expectedStateRevision])).toEqual([['xamlg_ui_state', 0], ['xamlg_ui_form_touch', 1]]);
  await name.focus(); await name.press('Enter');
  await expect(app.getByRole('region', { name: 'Review UI action' })).toBeVisible();
  expect(await page.evaluate(() => window.formHost.messages)).toEqual([]);
  await app.getByRole('button', { name: 'Confirm', exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.formHost.messages.length)).toBe(1);
  expect(await page.evaluate(() => window.formHost.messages[0].content)).toEqual([{ type: 'text', text: 'Send Ada' }]);
});

test('async validation leaves inputs editable and cannot submit changed values', async ({ page }) => {
  const app = await mount(page, true), name = app.getByRole('textbox', { name: 'Name', exact: true });
  await name.fill('Ada'); await name.press('Enter');
  await expect.poll(() => page.evaluate(() => window.formHost.pending)).toBe(true);
  await expect(name).toBeEnabled(); await expect(app.getByRole('button', { name: 'Send', exact: true })).toBeDisabled();
  await name.fill('Grace'); await name.press('Tab');
  await expect.poll(() => page.evaluate(() => window.formHost.name)).toBe('Grace');
  await page.evaluate(() => window.formHost.complete());
  await expect(app.getByRole('region', { name: 'Review UI action' })).toBeHidden();
  expect(await page.evaluate(() => window.formHost.messages)).toEqual([]);
  await name.focus(); await name.press('Enter');
  await expect.poll(() => page.evaluate(() => window.formHost.checks)).toBe(2);
  await page.evaluate(() => window.formHost.complete());
  await expect(app.getByRole('region', { name: 'Review UI action' })).toContainText('Send Grace');
  expect(await page.evaluate(() => window.formHost.messages)).toEqual([]);
});

test('teardown cancels pending form presentation and late validation cannot resurrect it', async ({ page }) => {
  const app = await mount(page, true), name = app.getByRole('textbox', { name: 'Name', exact: true });
  await name.fill('Ada'); await name.press('Enter');
  await expect.poll(() => page.evaluate(() => window.formHost.pending)).toBe(true);
  await page.evaluate(() => window.formHost.teardown());
  await expect(app.locator('#surface')).toBeEmpty();
  await page.evaluate(() => window.formHost.complete());
  await expect(app.locator('#surface')).toBeEmpty();
  expect(await page.evaluate(() => window.formHost.messages)).toEqual([]);
});
