import { test, expect } from './studio-fixture.mjs';
import { openStudio } from './live-preview.mjs';
import { agentSection, createAgentTask, reviewAgentRun } from './agent-fixture.mjs';
import { providerEvents } from './agent-provider-fixtures.mjs';

const xaml = '<StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui" Spacing="12"><Slider ui:Key="seats" ui:Bind="seats" Minimum="1" Maximum="50"/><TextBlock ui:Key="price" Text="{ui:Expr &quot;$&quot; + state.seats * data.unitPrice}"/></StackPanel>';

test('intelligent UI demo uses native Avalonia without a provider and exports reactive source', async ({ page }) => {
  const requests = [];
  page.on('request', request => { if (/api.openai.com|api.anthropic.com|generativelanguage.googleapis.com/.test(request.url())) requests.push(request.url()); });
  await openStudio(page, false);
  await page.getByTestId('agent-workbench').click();
  const pane = page.getByRole('region', { name: 'Coding agent workbench' });
  await pane.locator('.intelligent-ui-demo > summary').click();
  await pane.getByRole('button', { name: 'Try intelligent UI', exact: true }).click();
  const card = pane.getByRole('article', { name: 'Intelligent UI response' });
  await expect(card.locator('.intelligent-ui-native canvas')).toBeVisible();
  await card.getByRole('button', { name: 'Inspect UI', exact: true }).click();
  const input = card.getByLabel('UI state seats', { exact: true });
  await input.fill('10'); await input.press('Tab');
  await expect(card.locator('header')).toContainText('state 1');
  const fallback = card.locator('details').filter({ has: page.locator('summary', { hasText: 'Computed text fallback' }) });
  await fallback.locator('summary').click(); await expect(fallback).toContainText('$290');
  const exported = page.waitForEvent('download');
  await card.getByRole('button', { name: 'Export reactive C#', exact: true }).click();
  const download = await exported;
  expect(download.suggestedFilename()).toBe('GeneratedIntelligentView.cs');
  const stream = await download.createReadStream(); const chunks = []; for await (const chunk of stream) chunks.push(chunk);
  const source = Buffer.concat(chunks).toString('utf8');
  expect(source).toContain('UiAvaloniaSession'); expect(source).toContain('UiSessionStore');
  expect(requests).toEqual([]);
});

const hosts = { openai: 'api.openai.com', anthropic: 'api.anthropic.com', gemini: 'generativelanguage.googleapis.com' };
for (const [provider, host] of Object.entries(hosts)) test(`direct ${provider} publishes a native intelligent UI card with a lossless tool continuation`, async ({ page }) => {
  const requests = [], errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route(`https://${host}/**`, async route => {
    const input = route.request().postDataJSON(); requests.push(input);
    const output = requests.length === 1 ? [{ type: 'function_call', id: 'fc_ui', call_id: 'call_ui', name: 'xamlg_ui_present',
      arguments: JSON.stringify({ id: 'agent-pricing', expectedRevision: 0, sequence: 1, xaml, initialState: { seats: 8 }, data: { unitPrice: 29 }, fallbackMarkdown: 'Interactive pricing' }), status: 'completed' }] :
      [{ type: 'message', id: 'msg_ui', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'Adjust the seats in the interactive card.', annotations: [] }] }];
    if (requests.length === 2) { expect(JSON.stringify(input)).toContain('xamlg.intelligent-ui/1'); expect(JSON.stringify(input)).toContain('sessionId'); }
    await route.fulfill({ contentType: 'text/event-stream', body: providerEvents(provider, output, requests.length).map(event => `data: ${JSON.stringify(event)}\n\n`).join('') });
  });
  await openStudio(page, false); await page.getByTestId('agent-workbench').click();
  const pane = page.getByRole('region', { name: 'Coding agent workbench' });
  await agentSection(pane, 'Connection');
  await pane.getByLabel('Provider', { exact: true }).selectOption(provider);
  await pane.getByLabel('API key', { exact: true }).fill('synthetic-ui-test-key');
  await pane.getByLabel('Accept browser key exposure').check();
  await createAgentTask(pane, 'Intelligent UI');
  await reviewAgentRun(page, pane, 'Present an interactive pricing card.');
  const review = pane.getByRole('region', { name: 'Agent request' });
  await expect(review).toContainText('xamlg_ui_present');
  await review.getByRole('button', { name: 'Allow once', exact: true }).click();
  await expect.poll(() => requests.length).toBe(2);
  await expect(pane.locator('.agent-task-status')).toContainText('completed');
  const card = pane.locator('.agent-thread .intelligent-ui-card');
  await expect(card.locator('.intelligent-ui-native canvas')).toBeVisible();
  await card.getByRole('button', { name: 'Inspect UI', exact: true }).click();
  await card.getByLabel('UI state seats', { exact: true }).fill('12');
  await card.getByLabel('UI state seats', { exact: true }).press('Tab');
  await expect(card.locator('header')).toContainText('state 1');
  const fallback = card.locator('details').filter({ has: page.locator('summary', { hasText: 'Computed text fallback' }) });
  await fallback.locator('summary').click(); await expect(fallback).toContainText('$348');
  expect(requests).toHaveLength(2); expect(errors).toEqual([]);
});
