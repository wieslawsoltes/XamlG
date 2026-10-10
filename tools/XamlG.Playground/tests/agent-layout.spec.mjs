import { test, expect as baseExpect } from './studio-fixture.mjs';
const expect = baseExpect.configure({ timeout: 20000 });
import { openStudio, call, writeDocument } from './live-preview.mjs';
import { agentSection, withAgentWorkbench, agentReply, sendAgentEvent, createAgentTask, reviewAgentRun, completedTask } from './agent-fixture.mjs';
import { providerEvents, modelCatalog } from './agent-provider-fixtures.mjs';

const sections = ['Conversation', 'Connection', 'Tasks', 'Plan', 'Changes', 'Queue', 'Permissions', 'Tools', 'Activity'];

test('tool cards present large results and errors without exposing raw JSON in the conversation', async ({ page, request, baseURL }, testInfo) => {
  test.setTimeout(120000);
  await withAgentWorkbench({ page, request, baseURL }, ({ response, sequence }) => {
    const output = sequence <= 2 ? [{ type: 'function_call', id: `read_${sequence}`, call_id: 'reused_call_id', name: 'xamlg_document_read',
      arguments: JSON.stringify({ path: sequence === 1 ? 'Code.cs' : 'Missing.cs' }), status: 'completed' }] : null;
    sendAgentEvent(response, agentReply('Source inspected. The missing file was reported.', sequence, false, output)); response.end();
  }, async ({ page, pane, api }) => {
    await writeDocument((name, args) => call(page, name, args), 'Code.cs', '// <img src=x onerror="window.toolOutputExecuted=true">\n' +
      Array.from({ length: 250 }, (_, i) => `// Source line ${i}: keep the complete result in provider history and exports.`).join('\n'));
    const id = await createAgentTask(pane, 'Inspect source results');
    await reviewAgentRun(page, pane, 'Read source and report a missing file.');
    await completedTask(api, id);
    const cards = pane.getByRole('log').locator('.agent-tool');
    await expect(cards).toHaveCount(2);
    await expect(cards.nth(0)).toHaveAttribute('data-status', 'Completed');
    await expect(cards.nth(1)).toHaveAttribute('data-status', 'Failed');
    for (const card of await cards.all()) {
      expect(await card.locator(':scope > summary').innerText()).not.toMatch(/[{}]|tool_started|tool_completed/);
      await expect(card.locator('pre').first()).not.toBeVisible();
    }
    await cards.first().locator(':scope > summary').click();
    await expect(cards.first().getByText('Structured excerpt. Export the thread for the complete result.')).toBeVisible();
    await expect(cards.first().locator('.agent-result-fields').first()).toContainText('Code.cs');
    await expect(cards.first().locator('.agent-raw-result pre')).not.toBeVisible();
    await expect(cards.first().locator('img, iframe, script')).toHaveCount(0);
    expect(await page.evaluate(() => window.toolOutputExecuted)).toBeUndefined();
    await cards.first().locator('.agent-raw-result > summary').click();
    await expect(cards.first().locator('.agent-raw-result pre')).toContainText('Source line 0');
    await cards.first().locator('.agent-raw-result > summary').click();
    await page.screenshot({ path: testInfo.outputPath('compact-agent-results.png') });
    const exported = JSON.parse(await api('export', { id }));
    expect(JSON.stringify(exported)).toContain('Source line 249');

    // Tool JSON is data: marker-shaped strings and large counts must not crash
    // the renderer or prevent reading the rest of the conversation.
    await page.route('**/agent/state', async route => {
      // Inject into a full snapshot, rather than the normal unchanged-state cursor.
      const response = await route.fetch({ postData: {} });
      try {
        const state = await response.json();
        const task = state.tasks.find(task => task.id === id);
        const last = task.events.at(-1);
        task.events.push({ ...last, sequence: last.sequence + 1, kind: 'tool_completed', toolName: 'xamlg_inspect_values',
          toolCallId: 'render-values', text: JSON.stringify({ values: [42, null, true, { $moreItems: 'ordinary value' },
            { $moreItems: -1 }, { $moreItems: 2147483647 }, { $moreItems: 2147483647 }] }) });
        await route.fulfill({ response, json: state });
      } finally { await response.dispose(); }
    });
    const follow = pane.getByRole('button', { name: 'Follow latest', exact: true });
    if (await follow.isVisible()) await follow.click();
    await pane.getByRole('button', { name: 'Refresh coding agent', exact: true }).click();
    const values = pane.getByRole('log').locator('.agent-tool').filter({ hasText: 'Inspect values' });
    await expect(values).toHaveAttribute('data-status', 'Completed');
    await values.locator(':scope > summary').click();
    await values.locator('.agent-result-branch > summary').first().click();
    await expect(values.locator('.agent-result-fields').first()).toContainText('ordinary value');
    await expect(values.locator('.agent-result-fields').first()).toContainText('4294967299 items');
  });
});

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
  await agentSection(pane, 'Permissions');
  await pane.getByLabel('Review every run before sending').check();
  await pane.getByLabel('Automatically send queued follow-ups').uncheck();
  await agentSection(pane, 'Conversation');
  await pane.getByLabel('Message', { exact: true }).fill('Prepare a follow-up without interrupting the current work.');
  if (await pane.locator('.agent-composer-more').getAttribute('open') === null) await pane.locator('.agent-composer-more > summary').click();
  await pane.getByRole('button', { name: 'Queue follow-up', exact: true }).click();
  await pane.getByLabel('Message', { exact: true }).fill('Plan a workspace review.');
  await pane.getByRole('button', { name: 'Send message', exact: true }).click();
  await page.getByRole('dialog', { name: 'Review agent run' }).getByRole('button', { name: 'Confirm run', exact: true }).click();
  await expect(pane.locator('.agent-task-status')).toContainText('completed');
  await expect(pane.getByRole('log')).toContainText('Review detail 20');
  const planCall = pane.getByRole('log').locator('.agent-tool');
  await expect(planCall).toHaveCount(1);
  await expect(planCall.locator(':scope > summary')).toContainText('Agent plan');
  await expect(planCall.locator(':scope > summary')).toContainText('Completed');
  expect(await planCall.locator(':scope > summary').innerText()).not.toMatch(/[{}]|tool_started|tool_completed/);
  await planCall.locator(':scope > summary').click();
  await expect(planCall.locator('.agent-result-fields').first()).toBeVisible();
  await expect(planCall.locator('.agent-raw-result pre')).not.toBeVisible();
  await planCall.locator('.agent-raw-result > summary').click();
  await expect(planCall.locator('.agent-raw-result pre')).toContainText('inspect');
  await planCall.locator(':scope > summary').click();
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
  for (const [selector, maximum] of [['.agent-heading', 48], ['.agent-navigation', 36], ['.agent-task-context', 44], ['.agent-composer', 140]]) {
    expect((await pane.locator(selector).boundingBox()).height, `${selector} remains compact`).toBeLessThanOrEqual(maximum);
  }
  const navigationRows = await pane.getByRole('navigation').locator(':scope > button').evaluateAll(buttons => new Set(buttons.map(button => Math.round(button.getBoundingClientRect().top))).size);
  expect(navigationRows).toBe(1);
  await expect(pane.getByLabel('Message', { exact: true })).toHaveValue('Keep this draft while browsing panels.');
  const composer = await pane.locator('.agent-composer').boundingBox(), bounds = await pane.boundingBox();
  expect(composer.y + composer.height).toBeLessThanOrEqual(bounds.y + bounds.height + 1);
  await pane.getByRole('log').evaluate(element => { element.scrollTop = 0; });
  await expect(pane.getByRole('button', { name: 'Send message', exact: true })).toBeInViewport();

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
