import { expect } from './studio-fixture.mjs';

export const call = (page, name, args = {}) => page.evaluate(({ name, args }) => window.xamlgAutomation.call(name, args), { name, args });
export const source = page => page.evaluate(() => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').getValue());

export async function openStudio(page, sharing = true) {
  page.setDefaultTimeout(20000);
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  if (sharing) {
    await page.getByTestId('agent-access').click();
    await page.getByLabel('Enable access to this live project').check();
    await page.getByLabel('Permission profile').selectOption('FullAccess');
    await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
  }
}

export async function setSourceAndRun(page, text) {
  await page.evaluate(value => {
    monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').setValue(value);
    document.querySelector('[data-testid="run-preview"]').click();
  }, text);
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await expect(page.locator('#avalonia-preview canvas').first()).toBeVisible();
}

export async function connectMcp(page, request) {
  const base = process.env.XAMLG_TEST_MCP_URL;
  if (!base) throw new Error('Run this scenario through scripts/test-browser-studio.py.');
  await page.getByTestId('agent-access').click();
  await page.getByLabel('Companion WebSocket').fill(base.replace('http:', 'ws:') + '/bridge');
  await page.getByLabel('Owner token').fill(process.env.XAMLG_TEST_OWNER_TOKEN);
  await page.getByRole('button', { name: 'Connect companion' }).click();
  await expect(page.locator('.agent-access-panel').getByRole('status')).toContainText('Connected');
  await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
  let sequence = 0, session;
  const headers = () => ({ Authorization: `Bearer ${process.env.XAMLG_TEST_MCP_TOKEN}`, Accept: 'application/json, text/event-stream',
    'MCP-Protocol-Version': '2025-11-25', ...(session ? { 'Mcp-Session-Id': session } : {}) });
  async function rpc(method, params = {}) {
    const id = ++sequence;
    const response = await request.post(base + '/mcp', { headers: headers(), data: { jsonrpc: '2.0', id, method, params } });
    try {
      expect(response.ok()).toBe(true);
      session ||= response.headers()['mcp-session-id'];
      const body = await response.text();
      const messages = body.startsWith('{') ? [JSON.parse(body)] : body.split('\n').filter(line => line.startsWith('data:')).map(line => JSON.parse(line.slice(5)));
      const message = messages.find(item => item.id === id);
      if (message?.error) throw new Error(JSON.stringify(message.error));
      expect(message).toBeTruthy(); return message.result;
    } finally { await response.dispose(); }
  }
  await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'XamlG live acceptance', version: '1' } });
  const initialized = await request.post(base + '/mcp', { headers: headers(), data: { jsonrpc: '2.0', method: 'notifications/initialized' } });
  expect(initialized.ok()).toBe(true); await initialized.dispose();
  return {
    rpc,
    async call(name, args = {}) {
      const result = await rpc('tools/call', { name, arguments: args });
      if (result.isError) throw new Error(result.content?.filter(item => item.type === 'text').map(item => item.text).join('\n') || 'MCP tool failed');
      return result.structuredContent;
    },
    async close() {
      if (session) {
        const response = await request.delete(base + '/mcp', { headers: headers() });
        expect(response.ok()).toBe(true); await response.dispose();
      }
    }
  };
}

export async function writeDocument(invoke, path, text) {
  const project = await invoke('xamlg_project_get');
  return invoke('xamlg_document_write', { path, text, expectedRevision: project.revision });
}

export async function mutateRuntime(invoke, name, args) {
  const tree = await invoke('xamlg_runtime_tree');
  return invoke(name, { ...args, expectedRevision: tree.revision });
}
