import { test, expect as baseExpect } from './studio-fixture.mjs';
const expect = baseExpect.configure({ timeout: 20000 });
import { openStudio } from './live-preview.mjs';
import { agentSection } from './agent-fixture.mjs';
import { providerEvents, modelCatalog } from './agent-provider-fixtures.mjs';

const sections = ['Conversation', 'Connection', 'Tasks', 'Plan', 'Changes', 'Queue', 'Permissions', 'Tools', 'Activity'];

test('agent onboarding and all panels remain usable in a narrow dock and floating window', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  let requests = 0;
  await page.route('https://api.openai.com/**', async route => {
    if (route.request().method() === 'GET') { await route.fulfill({ json: modelCatalog('openai') }); return; }
    requests++;
    const output = requests === 1 ? [{ type: 'function_call', id: 'plan', call_id: 'plan', name: 'xamlg_agent_plan', status: 'completed',
      arguments: JSON.stringify({ expectedRevision: 0, steps: [
        { id: 'inspect', text: 'Inspect the current project and its runtime.', status: 'completed' },
        { id: 'design', text: 'Review the proposed layout with the user.', status: 'inProgress' },
        { id: 'validate', text: 'Validate the final source and preview.', status: 'pending' }
      ] }) }] : [{ type: 'message', id: 'reply', role: 'assistant', status: 'completed', content: [{ type: 'output_text',
      text: '# Workspace review\n\n' + Array.from({ length: 20 }, (_, i) => `Review detail ${i + 1}: the current project is ready for your next request.`).join('\n\n'), annotations: [] }] }];
    await route.fulfill({ contentType: 'text/event-stream', body: providerEvents('openai', output, requests).map(event => `data: ${JSON.stringify(event)}\n\n`).join('') });
  });
  await openStudio(page, false);
  await page.getByTestId('agent-workbench').click();
  const pane = page.getByRole('region', { name: 'Coding agent workbench' });
  await expect(pane.getByLabel('API key', { exact: true })).toBeVisible();
  await expect(pane.getByLabel('Agent connection', { exact: true })).toHaveValue('direct');
  await pane.getByLabel('API key', { exact: true }).fill('synthetic-layout-key');
  await pane.getByLabel('Accept browser key exposure').check();
  await pane.getByRole('button', { name: 'Discover models', exact: true }).click();
  await expect(pane.locator('#agent-models option')).toHaveCount(1);
  await pane.getByLabel('Model', { exact: true }).fill('test-model');
  await pane.getByRole('button', { name: 'Continue to tasks' }).click();
  await pane.getByLabel('Task name', { exact: true }).fill('Review the workspace layout');
  await pane.getByRole('button', { name: 'Create task', exact: true }).click();
  await pane.getByLabel('Message', { exact: true }).fill('Prepare a follow-up without interrupting the current work.');
  await pane.getByRole('button', { name: 'Queue follow-up', exact: true }).click();
  await pane.getByLabel('Message', { exact: true }).fill('Plan a workspace review.');
  await pane.getByRole('button', { name: 'Run', exact: true }).click();
  await page.getByRole('dialog', { name: 'Review agent run' }).getByRole('button', { name: 'Confirm run', exact: true }).click();
  await expect(pane.locator('.agent-task-status')).toContainText('completed');
  await expect(pane.getByRole('log')).toContainText('Review detail 20');
  expect(requests).toBe(2);
  await pane.getByLabel('Message', { exact: true }).fill('Keep this draft while browsing panels.');
  await agentSection(pane, 'Plan');
  await expect(pane.getByRole('progressbar', { name: 'Plan progress' })).toHaveAttribute('value', '1');
  await expect(pane.locator('.agent-plan li')).toHaveCount(3);
  await agentSection(pane, 'Tasks');
  await expect(pane.locator('.agent-task-card')).toHaveAttribute('aria-pressed', 'true');
  await agentSection(pane, 'Tools');
  await pane.getByLabel('Search tools', { exact: true }).fill('no-such-tool');
  await expect(pane.getByRole('heading', { name: 'No matching tools' })).toBeVisible();
  await pane.getByRole('button', { name: 'Clear tool filters' }).click();
  await expect(pane.locator('.agent-catalog-tool').first()).toBeVisible();

  for (const theme of ['dark', 'light']) {
    if (theme === 'light') await page.getByRole('button', { name: 'Toggle color theme' }).click();
    await page.setViewportSize({ width: 740, height: 820 });
    for (const section of sections) {
      await agentSection(pane, section);
      await expect(pane.getByRole('navigation').getByRole('button', { name: section, exact: true })).toBeVisible();
      expect(await pane.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
      expect(await pane.locator('.agent-content').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
    }
  }
  await agentSection(pane, 'Conversation');
  await expect(pane.getByLabel('Message', { exact: true })).toHaveValue('Keep this draft while browsing panels.');
  const composer = await pane.locator('.agent-composer').boundingBox(), bounds = await pane.boundingBox();
  expect(composer.y + composer.height).toBeLessThanOrEqual(bounds.y + bounds.height + 1);
  await pane.getByRole('log').evaluate(element => { element.scrollTop = 0; });
  await expect(pane.getByRole('button', { name: 'Run', exact: true })).toBeInViewport();

  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.locator('[data-tab-id="agent"]').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Float', exact: true }).click();
  await expect(pane).toBeVisible();
  await expect(pane.getByLabel('Message', { exact: true })).toHaveValue('Keep this draft while browsing panels.');
  await agentSection(pane, 'Connection');
  await pane.getByLabel('Agent connection', { exact: true }).selectOption('companion');
  await pane.getByRole('button', { name: 'Open Agent access', exact: true }).click();
  await expect(page.locator('.agent-access-panel')).toBeVisible();
  expect(errors).toEqual([]);
});
