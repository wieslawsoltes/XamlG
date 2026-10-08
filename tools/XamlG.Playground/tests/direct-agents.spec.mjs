import { test, expect as baseExpect } from './studio-fixture.mjs';
import { providerEvents, modelCatalog } from './agent-provider-fixtures.mjs';
import { openStudio } from './live-preview.mjs';
import { createAgentTask } from './agent-fixture.mjs';

const key = 'synthetic-browser-key-no-billing';
const expect = baseExpect.configure({ timeout: 15000 });
const hosts = { openai: 'api.openai.com', anthropic: 'api.anthropic.com', gemini: 'generativelanguage.googleapis.com' };

const section = (pane, name) => pane.getByRole('navigation', { name: 'Agent sections' }).getByRole('button', { name, exact: true }).click();

for (const provider of Object.keys(hosts)) {
test(`direct ${provider} uses browser SDK, source approval and native tool continuation without MCP`, async ({ page }) => {
  test.setTimeout(90000); page.setDefaultTimeout(15000);
  const requests = [], errors = [], network = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (/\/agent\/|\/bridge|\/provider\//.test(request.url())) network.push(request.url()); });
  const after = '<TextBlock xmlns="https://github.com/avaloniaui" Text="Direct browser edit" />';
  await page.route(`https://${hosts[provider]}/**`, async route => {
    const request = route.request(), headers = request.headers();
    expect(request.url()).not.toContain(key);
    expect(headers[provider === 'openai' ? 'authorization' : provider === 'anthropic' ? 'x-api-key' : 'x-goog-api-key']).toBe(provider === 'openai' ? `Bearer ${key}` : key);
    if (provider === 'anthropic') expect(headers['anthropic-dangerous-direct-browser-access']).toBe('true');
    if (request.method() === 'GET') { await route.fulfill({ json: modelCatalog(provider) }); return; }
    const input = request.postDataJSON(); requests.push(input);
    const round = requests.length;
    const call = (name, args) => ({ type: 'function_call', id: `fc_${round}`, call_id: `call_${round}`, name, arguments: JSON.stringify(args), status: 'completed' });
    const result = id => provider === 'openai' ? JSON.parse(input.input.find(item => item.type === 'function_call_output' && item.call_id === id).output) :
      provider === 'anthropic' ? JSON.parse(input.messages.flatMap(message => Array.isArray(message.content) ? message.content : []).find(item => item.type === 'tool_result' && item.tool_use_id === id).content) :
      input.contents.flatMap(message => message.parts).find(item => item.functionResponse?.id === id).functionResponse.response;
    const output = round === 1 ? [call('xamlg_project_get', {})] : round === 2 ? [call('xamlg_document_write', { path: 'View.axaml', text: after, expectedRevision: result('call_1').revision })] :
      round === 3 ? [call('xamlg_compiler_compile', {})] : [{ type: 'message', id: 'msg_done', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'Browser edit compiled successfully.', annotations: [] }] }];
    if (round === 4) expect(result('call_3').success).toBe(true);
    await route.fulfill({ contentType: 'text/event-stream', body: providerEvents(provider, output, round).map(event => `data: ${JSON.stringify(event)}\n\n`).join('') });
  });
  await openStudio(page, false);
  await page.getByTestId('agent-workbench').click();
  const pane = page.getByRole('region', { name: 'Coding agent workbench' });
  await section(pane, 'Connection');
  await pane.getByLabel('Provider', { exact: true }).selectOption(provider);
  await pane.getByLabel('API key', { exact: true }).fill(key);
  await expect(pane.getByRole('button', { name: 'Discover models', exact: true })).toBeDisabled();
  await pane.getByLabel('Accept browser key exposure').check();
  await pane.getByRole('button', { name: 'Discover models', exact: true }).click();
  await expect(pane.locator('#agent-models option')).toHaveCount(1);
  await pane.getByLabel('Model', { exact: true }).fill('test-model');
  await section(pane, 'Tasks');
  await pane.getByLabel('Task name', { exact: true }).fill('Direct SDK test');
  await pane.getByRole('button', { name: 'Create task', exact: true }).click();
  await pane.getByLabel('Message', { exact: true }).fill('Change and compile the current XAML.');
  await pane.getByRole('button', { name: 'Run', exact: true }).click();
  await page.getByRole('dialog', { name: 'Review agent run' }).getByRole('button', { name: 'Confirm run', exact: true }).click();
  await expect(pane.locator('.agent-pending')).toContainText('Direct browser edit');
  await expect(pane.locator('.agent-pending')).toContainText('Before');
  await expect(pane.locator('.agent-pending')).toContainText('After');
  const download = page.waitForEvent('download');
  await pane.getByRole('button', { name: 'Save full review' }).click();
  expect((await download).suggestedFilename()).toContain('review');
  await pane.getByRole('button', { name: 'Allow once', exact: true }).click();
  await expect(pane.locator('.agent-task-status')).toContainText('completed', { timeout: 30000 });
  expect(requests).toHaveLength(4); expect(network).toEqual([]); expect(errors).toEqual([]);
  await expect(pane.locator('.agent-thread')).toContainText('Browser edit compiled successfully.');
  await section(pane, 'Tools');
  await pane.getByLabel('Search tools', { exact: true }).fill('xamlg_document_write');
  await expect(pane.locator('.agent-catalog-tool')).toContainText('xamlg_document_write');
  await section(pane, 'Conversation');
  await pane.getByLabel('Message', { exact: true }).fill('Draft survives a restart');
  await expect.poll(() => page.evaluate(async () => {
    const saved = await (await xamlgBoot.importModule('studio.js')).loadStudioState('agent-ui');
    return Object.values(saved?.drafts || {}).includes('Draft survives a restart');
  })).toBe(true);
  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.getByTestId('agent-workbench').click();
  await section(pane, 'Conversation');
  await expect(pane.locator('.agent-thread')).toContainText('Browser edit compiled successfully.');
  await expect(pane.getByLabel('Message', { exact: true })).toHaveValue('Draft survives a restart');
  await section(pane, 'Connection');
  await expect(pane.getByLabel('API key', { exact: true })).toHaveValue(key);
  await expect(pane.getByLabel('Accept browser key exposure')).toBeChecked();
  expect(requests).toHaveLength(4);
  await pane.getByRole('button', { name: 'Forget connection', exact: true }).click();
  await expect(pane.getByLabel('API key', { exact: true })).toHaveValue('');
  const stored = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
  expect(stored).not.toContain(key);
  await page.getByTestId('agent-access').click();
  await expect(page.getByLabel('Enable access to this live project')).not.toBeChecked();
});
}

test('direct credentials are remembered per provider while closure and workspace replacement revoke the active run', async ({ page }) => {
  test.setTimeout(90000); page.setDefaultTimeout(15000);
  const response = Promise.withResolvers(), arrived = Promise.withResolvers();
  let calls = 0;
  await page.route('https://api.openai.com/**', async route => {
    if (route.request().method() === 'GET') { await route.fulfill({ json: modelCatalog('openai') }); return; }
    calls++; arrived.resolve(); await response.promise;
    try {
      await route.fulfill({ contentType: 'text/event-stream', body: providerEvents('openai', [{ type: 'message', id: 'late', role: 'assistant', status: 'completed',
        content: [{ type: 'output_text', text: 'Late response must not complete the cancelled task.', annotations: [] }] }], 1).map(event => `data: ${JSON.stringify(event)}\n\n`).join('') });
    } catch { /* The browser may already have aborted this deliberately delayed request. */ }
  });
  try {
    await openStudio(page, false);
    await page.getByTestId('agent-workbench').click();
    const pane = page.getByRole('region', { name: 'Coding agent workbench' });
    await section(pane, 'Connection');
    await pane.getByLabel('API key', { exact: true }).fill(key);
    await pane.getByLabel('Accept browser key exposure').check();
    const first = await createAgentTask(pane, 'First provider');
    await section(pane, 'Connection');
    await pane.getByLabel('Provider', { exact: true }).selectOption('anthropic');
    await expect(pane.getByLabel('API key', { exact: true })).toHaveValue('');
    await expect(pane.getByLabel('Accept browser key exposure')).not.toBeChecked();
    await pane.getByLabel('API key', { exact: true }).fill('synthetic-second-provider');
    await pane.getByLabel('Accept browser key exposure').check();
    await createAgentTask(pane, 'Second provider');
    await pane.getByLabel('Task', { exact: true }).selectOption(first);
    await section(pane, 'Connection');
    await expect(pane.getByLabel('Provider', { exact: true })).toHaveValue('openai');
    await expect(pane.getByLabel('API key', { exact: true })).toHaveValue(key);
    await pane.getByLabel('API key', { exact: true }).fill(key);
    await pane.getByLabel('Accept browser key exposure').check();
    await section(pane, 'Conversation');
    await pane.getByLabel('Message', { exact: true }).fill('Wait for a response.');
    await pane.getByRole('button', { name: 'Run', exact: true }).click();
    await page.getByRole('dialog', { name: 'Review agent run' }).getByRole('button', { name: 'Confirm run', exact: true }).click();
    await arrived.promise;
    await page.locator('.ad-anchorable-pane[aria-label="Coding agent"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
    response.resolve();
    await page.getByTestId('agent-workbench').click();
    await expect(pane.locator('.agent-task-status')).toContainText('cancelled');
    await expect(pane.locator('.agent-assistant')).toHaveCount(0);
    await section(pane, 'Connection');
    await expect(pane.getByLabel('API key', { exact: true })).toHaveValue(key);
    await expect(pane.getByLabel('Accept browser key exposure')).toBeChecked();
    await pane.getByLabel('API key', { exact: true }).fill(key);
    await pane.getByLabel('Accept browser key exposure').check();
    await page.locator('.studio-menu > summary').filter({ hasText: /^Project$/ }).click();
    await page.getByLabel('Example', { exact: true }).selectOption('1');
    await page.keyboard.press('Escape');
    await expect(pane.getByLabel('API key', { exact: true })).toHaveValue(key);
    await expect(pane.locator('.agent-task-status')).toContainText('Previous workspace');
    expect(calls).toBe(1);
    expect(await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]))).not.toContain(key);
    await page.getByTestId('agent-access').click();
    await expect(page.getByLabel('Enable access to this live project')).not.toBeChecked();
  } finally { response.resolve(); }
});
