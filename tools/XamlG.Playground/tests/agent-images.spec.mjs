import { test, expect } from './studio-fixture.mjs';
import { openStudio } from './live-preview.mjs';
import { agentSection, createAgentTask, reviewAgentRun } from './agent-fixture.mjs';
import { providerEvents } from './agent-provider-fixtures.mjs';

const hosts = { openai: 'api.openai.com', anthropic: 'api.anthropic.com', gemini: 'generativelanguage.googleapis.com' };
for (const [provider, host] of Object.entries(hosts)) test(`direct ${provider} receives native preview images and shows screenshot cards`, async ({ page }) => {
  const requests = [], errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route(`https://${host}/**`, async route => {
    const input = route.request().postDataJSON(); requests.push(input);
    if (requests.length === 2) {
      const parts = provider === 'openai' ? input.input.find(item => item.type === 'function_call_output').output :
        provider === 'anthropic' ? input.messages.flatMap(message => message.content).find(part => part.type === 'tool_result').content :
        input.contents.flatMap(message => message.parts);
      const image = parts.find(part => part.type === 'input_image' || part.type === 'image' || part.inlineData);
      expect(image).toBeTruthy();
      const base64 = image.image_url?.split(',')[1] || image.source?.data || image.inlineData?.data;
      expect([...Buffer.from(base64, 'base64').subarray(0, 4)]).toEqual([137, 80, 78, 71]);
    }
    const output = requests.length === 1 ? [{ type: 'function_call', id: 'fc_observe', call_id: 'call_observe', name: 'xamlg_computer_observe', arguments: '{}', status: 'completed' }] :
      [{ type: 'message', id: 'msg_seen', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'The preview is visible.', annotations: [] }] }];
    await route.fulfill({ contentType: 'text/event-stream', body: providerEvents(provider, output, requests.length).map(event => `data: ${JSON.stringify(event)}\n\n`).join('') });
  });
  await openStudio(page, false);
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await page.getByTestId('agent-workbench').click();
  const pane = page.getByRole('region', { name: 'Coding agent workbench' });
  await agentSection(pane, 'Connection');
  await pane.getByLabel('Provider', { exact: true }).selectOption(provider);
  await pane.getByLabel('API key', { exact: true }).fill('synthetic-image-test-key');
  await pane.getByLabel('Accept browser key exposure').check();
  await createAgentTask(pane, 'Inspect image');
  await reviewAgentRun(page, pane, 'Observe the running preview.');
  await expect.poll(() => requests.length).toBe(2);
  await page.getByTestId('agent-workbench').click();
  await expect(pane.locator('.agent-task-status')).toContainText('completed');
  await pane.locator('.agent-event-card:has(img[alt="Preview screenshot"]) > summary').click();
  const image = pane.getByRole('img', { name: 'Preview screenshot' });
  await expect(image).toBeVisible();
  await expect.poll(() => image.evaluate(element => element.complete && element.naturalWidth > 0)).toBe(true);
  await expect(pane.locator('.agent-thread')).toContainText('The preview is visible.');
  expect(await pane.locator('.agent-thread').innerText()).not.toContain('iVBORw0');
  expect(errors).toEqual([]);
});
