import { test, expect as baseExpect } from './studio-fixture.mjs';
import { call, writeDocument } from './live-preview.mjs';
import { withAgentWorkbench, agentReply, agentDelta, sendAgentEvent, createAgentTask, reviewAgentRun, completedTask, agentSection } from './agent-fixture.mjs';

const expect = baseExpect.configure({ timeout: 15000 });
const reply = ({ response, sequence }) => { sendAgentEvent(response, agentReply('Public report from the fixture.', sequence)); response.end(); };
const options = pane => pane.locator('details.agent-options');
async function showOptions(pane) { await agentSection(pane, 'Permissions'); if (await options(pane).getAttribute('open') === null) await options(pane).locator('summary').click(); }
async function reopen(page) {
  await page.locator('.ad-anchorable-pane[aria-label="Coding agent"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
  await page.getByTestId('agent-workbench').click();
}

test('review feedback, diff paging and task drafts remain independent across task and pane changes', async ({ page, request, baseURL }) => {
  test.setTimeout(180000);
  await withAgentWorkbench({ page, request, baseURL }, reply, async ({ page, pane, api, requests }) => {
    const invoke = (name, args) => call(page, name, args);
    const baseline = '<StackPanel xmlns="https://github.com/avaloniaui">\n' +
      Array.from({ length: 620 }, (_, i) => `  <TextBlock Text="Before ${i}" />\n`).join('') + '</StackPanel>';
    await writeDocument(invoke, 'View.axaml', baseline);
    const first = await createAgentTask(pane, 'First review task');
    await reviewAgentRun(page, pane, 'Inspect the first task source.'); await completedTask(api, first);
    await showOptions(pane);
    await agentSection(pane, 'Permissions');
    await pane.getByLabel('Task permission profile').selectOption('autoEdit');
    await agentSection(pane, 'Permissions');
    await pane.getByLabel('Requests per run', { exact: true }).fill('17');
    await pane.getByLabel('Permission tool', { exact: true }).selectOption('xamlg_document_write');
    await pane.getByLabel('Tool permission decision', { exact: true }).selectOption('deny');
    await pane.getByRole('button', { name: 'Set tool rule', exact: true }).click();
    await agentSection(pane, 'Permissions');
    await pane.getByRole('button', { name: 'Save numeric defaults', exact: true }).click();
    await agentSection(pane, 'Conversation');
    await pane.getByLabel('Message', { exact: true }).fill('Private first composer draft');
    const after = baseline.replace('Before 405"', 'After 405"').replace('Before 605"', 'After 605"');
    await writeDocument(invoke, 'View.axaml', after);
    await agentSection(pane, 'Changes');
    await pane.getByRole('button', { name: 'Refresh changes', exact: true }).click();
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Selected change block')).toHaveText('Change 1 of 2');
    await pane.getByLabel(/View.axaml \(/).check();
    await pane.locator('.agent-diff .diff-added').filter({ hasText: 'After 405"' }).click();
    await agentSection(pane, 'Changes');
    await pane.getByLabel('Review feedback', { exact: true }).fill('Private first review feedback');

    const second = await createAgentTask(pane, 'Second review task');
    await showOptions(pane);
    await agentSection(pane, 'Permissions');
    await expect(pane.getByLabel('Task permission profile')).toHaveValue('ask');
    await agentSection(pane, 'Permissions');
    await expect(pane.getByLabel('Requests per run', { exact: true })).toHaveValue('17');
    await expect(pane.locator('.agent-rule')).toHaveCount(0);
    await reviewAgentRun(page, pane, 'Inspect the second task source.'); await completedTask(api, second);
    await writeDocument(invoke, 'Code.cs', 'public static class ReviewMarker { public const int Value = 2; }');
    await agentSection(pane, 'Changes');
    await pane.getByRole('button', { name: 'Refresh changes', exact: true }).click();
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Review document')).toHaveValue('Code.cs');
    await agentSection(pane, 'Changes');
    await pane.getByLabel('Review feedback', { exact: true }).fill('Private second review feedback');
    await agentSection(pane, 'Conversation');
    await pane.getByLabel('Message', { exact: true }).fill('Private second composer draft');
    await pane.getByLabel('Task', { exact: true }).selectOption(first);
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Review document')).toHaveValue('View.axaml');
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Review feedback', { exact: true })).toHaveValue('Private first review feedback');
    await agentSection(pane, 'Conversation');
    await expect(pane.getByLabel('Message', { exact: true })).toHaveValue('Private first composer draft');
    await agentSection(pane, 'Permissions');
    await expect(pane.getByLabel('Task permission profile')).toHaveValue('autoEdit');
    await agentSection(pane, 'Changes');
    await expect(pane.getByRole('button', { name: 'Queue review feedback', exact: true })).toBeDisabled();
    await agentSection(pane, 'Changes');
    await pane.getByRole('button', { name: 'Refresh changes', exact: true }).click();
    // An unrelated file changed: the line target and selected restore file remain valid after refresh.
    await expect(pane.getByText('A selected diff line is attached to this feedback.', { exact: true })).toBeVisible();
    await expect(pane.getByLabel(/View.axaml \(/)).toBeChecked();
    await agentSection(pane, 'Changes');
    await pane.getByRole('button', { name: 'Next change', exact: true }).click();
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Selected change block')).toHaveText('Change 2 of 2');
    await pane.locator('.agent-diff .diff-added').filter({ hasText: 'After 605"' }).click();
    await reopen(page);
    await expect(pane.getByLabel('Task', { exact: true })).toHaveValue(first);
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Selected change block')).toHaveText('Change 2 of 2');
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Review feedback', { exact: true })).toHaveValue('Private first review feedback');
    await agentSection(pane, 'Changes');
    await pane.getByRole('button', { name: 'Queue review feedback', exact: true }).click();
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Review feedback', { exact: true })).toHaveValue('');
    const task = (await api('state')).tasks.find(task => task.id === first);
    expect(task.queue.messages).toHaveLength(1);
    const feedback = JSON.parse(task.queue.messages[0].text.split('\n').slice(1).join('\n'));
    expect(feedback).toMatchObject({ baseline: 'task start', path: 'View.axaml', feedback: 'Private first review feedback',
      target: { Path: 'View.axaml', AfterLine: 607, Text: '  <TextBlock Text="After 605" />', Revision: task.changes.revision } });
    expect(JSON.stringify(requests)).not.toContain('Private first');
    const exported = await api('export', { id: first });
    expect(exported).not.toContain('Private first');
    await pane.getByLabel('Task', { exact: true }).selectOption(second);
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Review document')).toHaveValue('Code.cs');
    await agentSection(pane, 'Changes');
    await expect(pane.getByLabel('Review feedback', { exact: true })).toHaveValue('Private second review feedback');
    await agentSection(pane, 'Conversation');
    await expect(pane.getByLabel('Message', { exact: true })).toHaveValue('Private second composer draft');
    await pane.getByLabel('Task', { exact: true }).selectOption(first);
    await agentSection(pane, 'Tasks');
    await pane.getByRole('button', { name: 'New task with context', exact: true }).click();
    const handoff = page.getByRole('dialog', { name: 'Review task context' });
    expect(await handoff.getByLabel('Context handoff').inputValue()).toContain('Inspect the first task source.');
    expect(await handoff.getByLabel('Context handoff').inputValue()).not.toContain('Private first');
    await handoff.getByLabel('Context handoff').fill('Reviewed public handoff 🧭');
    await handoff.getByLabel('New task name').fill('Reviewed successor');
    await handoff.getByLabel('Model', { exact: true }).fill('test-model');
    await handoff.getByRole('button', { name: 'Create with reviewed context', exact: true }).click();
    await expect(handoff).not.toBeVisible();
    await agentSection(pane, 'Conversation');
    await expect(pane.getByLabel('Message', { exact: true })).toHaveValue('Reviewed public handoff 🧭');
    await showOptions(pane);
    await agentSection(pane, 'Permissions');
    await expect(pane.getByLabel('Task permission profile')).toHaveValue('ask');
    expect(requests).toHaveLength(2);
    const stored = await page.evaluate(() => JSON.parse(localStorage.getItem('xamlg.agent.numeric.v1')));
    expect(stored.requests).toBe(17); expect(Object.values(stored).every(value => Number.isInteger(value))).toBe(true);
    await pane.getByLabel('Task', { exact: true }).selectOption(first);
    // A larger changed interval deliberately selects the bounded coarse diff path.
    await writeDocument(invoke, 'View.axaml', baseline.replace('Before 5"', 'After 5"').replace('Before 605"', 'After 605"'));
    await agentSection(pane, 'Changes');
    await pane.getByRole('button', { name: 'Refresh changes', exact: true }).click();
    await expect(pane.locator('.agent-diff button')).toHaveCount(500);
    await agentSection(pane, 'Changes');
    await pane.getByRole('button', { name: 'Next diff lines', exact: true }).click();
    await expect(pane.getByText(/^Rows 501–1000 of /)).toBeVisible();
    await agentSection(pane, 'Changes');
    await pane.getByRole('button', { name: 'Previous diff lines', exact: true }).click();
    await expect(pane.getByText(/^Rows 1–500 of /)).toBeVisible();
    await agentSection(pane, 'Changes');
    await pane.getByRole('button', { name: 'Show more diff lines', exact: true }).click();
    await expect(pane.locator('.agent-diff button')).toHaveCount(1000);
  });
});

test('historical pages, expanded tools and reading positions survive late responses, task switches and new events', async ({ page, request, baseURL }) => {
  test.setTimeout(120000);
  const pageResponse = Promise.withResolvers(), pageArrived = Promise.withResolvers();
  const stateResponse = Promise.withResolvers(), stateArrived = Promise.withResolvers();
  try {
    await withAgentWorkbench({ page, request, baseURL }, ({ response, sequence }) => {
      sendAgentEvent(response, agentReply('The project was inspected.', sequence, false, sequence === 1
        ? [{ type: 'function_call', id: 'read_project', call_id: 'read_project', name: 'xamlg_project_get', arguments: '{}', status: 'completed' }] : null));
      response.end();
    }, async ({ page, pane, api }) => {
      const first = await createAgentTask(pane, 'History task');
      await reviewAgentRun(page, pane, 'History task public request.'); await completedTask(api, first);
      // Populate retained public history through real queue operations, without paying for more model turns.
      await page.evaluate(async id => {
        const studio = await window.xamlgBoot.importModule('studio.js');
        let queue = await studio.agentRequest('queue', { id, text: 'Never sent queue message' });
        for (let i = 0; i < 125; i++) queue = await studio.agentRequest('queue_edit', { id, messageId: queue.messages[0].id,
          expectedRevision: queue.revision, text: `Never sent queue message ${i}` });
      }, first);
      await pane.getByRole('button', { name: 'Refresh coding agent', exact: true }).click();
      const latest = (await api('state')).tasks.find(task => task.id === first).events;
      const earlier = await api('thread', { id: first, beforeSequence: latest[0].sequence });
      // A background snapshot captured before creation must not leave the newly
      // selected task unnamed or overwrite it when the delayed response arrives.
      let heldState = false;
      await page.route('**/agent/state', async route => {
        if (heldState) { await route.continue(); return; }
        heldState = true;
        const response = await route.fetch(); stateArrived.resolve();
        await stateResponse.promise; await route.fulfill({ response });
      });
      await pane.getByRole('button', { name: 'Refresh coding agent', exact: true }).click();
      await stateArrived.promise;
      await page.route('**/agent/create', async route => {
        const response = await route.fetch();
        await route.fulfill({ response }); stateResponse.resolve();
      });
      const second = await createAgentTask(pane, 'Independent task');
      await pane.getByLabel('Task', { exact: true }).selectOption(first);
      await page.route('**/agent/thread', async route => {
        const response = await route.fetch(); pageArrived.resolve();
        await pageResponse.promise; await route.fulfill({ response });
      });
      await agentSection(pane, 'Conversation');
      await pane.getByRole('button', { name: 'Earlier messages', exact: true }).click();
      await pageArrived.promise;
      await pane.getByLabel('Task', { exact: true }).selectOption(second);
      pageResponse.resolve();
      await expect(pane.getByRole('log')).not.toContainText('History task public request.');
      await pane.getByLabel('Task', { exact: true }).selectOption(first);
      const thread = pane.getByRole('log');
      // A completed tool shares its card with the start event; both events remain represented.
      const sequences = () => thread.locator('[data-event-sequences]').evaluateAll(nodes => nodes.flatMap(node => node.dataset.eventSequences.split(',').map(Number)).sort((a, b) => a - b));
      await expect.poll(sequences).toEqual(earlier.events.map(event => event.sequence));
      await expect(thread).toContainText('History task public request.');
      const tool = thread.locator('details.agent-tool').first();
      await tool.locator(':scope > summary').click(); await expect(tool).toHaveAttribute('open', '');
      await pane.getByLabel('Task', { exact: true }).selectOption(second);
      await pane.getByLabel('Task', { exact: true }).selectOption(first);
      await expect(tool).toHaveAttribute('open', '');
      await reopen(page);
      await expect.poll(sequences).toEqual(earlier.events.map(event => event.sequence));
      await expect(tool).toHaveAttribute('open', '');
      await thread.evaluate(element => { element.scrollTop = 100; });
      await expect(pane.getByText('Reading an earlier page. Follow latest returns to the current response.', { exact: true })).toBeVisible();
      const scroll = await thread.evaluate(element => element.scrollTop);
      const queue = (await api('state')).tasks.find(task => task.id === first).queue;
      await api('queue_edit', { id: first, messageId: queue.messages[0].id, expectedRevision: queue.revision, text: 'Another unsent edit while reading history' });
      await pane.getByRole('button', { name: 'Refresh coding agent', exact: true }).click();
      expect(await sequences()).toEqual(earlier.events.map(event => event.sequence));
      expect(Math.abs(await thread.evaluate(element => element.scrollTop) - scroll)).toBeLessThan(2);
      await agentSection(pane, 'Conversation');
      await pane.getByRole('button', { name: 'Newer messages', exact: true }).click();
      await expect.poll(async () => (await sequences())[0]).toBeGreaterThan(earlier.events.at(-1).sequence);
      await agentSection(pane, 'Conversation');
      await pane.getByRole('button', { name: 'Follow latest', exact: true }).click();
      await expect(pane.getByText('Reading an earlier page. Follow latest returns to the current response.', { exact: true })).not.toBeVisible();
      const current = (await api('state')).tasks.find(task => task.id === first);
      await expect.poll(sequences).toEqual(current.events.map(event => event.sequence));
      await expect.poll(() => thread.evaluate(element => element.scrollHeight - element.scrollTop - element.clientHeight)).toBeLessThan(2);
    });
  } finally { pageResponse.resolve(); stateResponse.resolve(); }
});

test('output-limited replies stay visibly incomplete and reviewed resume excludes their partial native text', async ({ page, request, baseURL }) => {
  test.setTimeout(90000);
  const partial = 'Partial public output that must not enter the next native request.';
  await withAgentWorkbench({ page, request, baseURL }, ({ response, sequence }) => {
    if (sequence === 1) sendAgentEvent(response, agentDelta(partial));
    sendAgentEvent(response, agentReply(sequence === 1 ? partial : 'Completed after reviewed resume.', sequence + 1, sequence === 1));
    response.end();
  }, async ({ page, pane, api, requests }) => {
    const id = await createAgentTask(pane, 'Output recovery');
    await reviewAgentRun(page, pane, 'Produce the final response.');
    await expect(pane.locator('.agent-assistant_incomplete')).toContainText(partial);
    await expect(pane.locator('.agent-task-status')).toContainText('paused');
    await showOptions(pane);
    await agentSection(pane, 'Permissions');
    await pane.getByLabel('Output tokens per request', { exact: true }).fill('65536');
    await agentSection(pane, 'Conversation');
    await pane.getByRole('button', { name: 'Resume', exact: true }).click();
    const review = page.getByRole('dialog', { name: 'Review agent run' });
    await expect(review).toContainText('65,536 output tokens');
    await review.getByRole('button', { name: 'Confirm run', exact: true }).click();
    await completedTask(api, id);
    await expect(pane.locator('.agent-assistant')).toContainText('Completed after reviewed resume.');
    expect(requests).toHaveLength(2);
    expect(JSON.stringify(requests[1])).not.toContain(partial);
    expect(requests[1].max_output_tokens).toBe(65536);
    expect(await api('export', { id })).toContain(partial);
  });
});

test('Stop cancels the provider stream and retains its incomplete public reply for review', async ({ page, request, baseURL }) => {
  test.setTimeout(90000);
  let closed = false;
  await withAgentWorkbench({ page, request, baseURL }, ({ response }) => {
    response.once('close', () => { closed = true; });
    sendAgentEvent(response, agentDelta('Streamed draft before Stop.'));
  }, async ({ page, pane, api, requests }) => {
    const id = await createAgentTask(pane, 'Cancelled stream');
    await reviewAgentRun(page, pane, 'Stream a response until cancelled.');
    await expect(pane.getByRole('log')).toContainText('Streamed draft before Stop.');
    await agentSection(pane, 'Conversation');
    await pane.getByRole('button', { name: 'Stop and keep progress', exact: true }).click();
    await expect(pane.locator('.agent-task-status')).toContainText('paused');
    await expect(pane.locator('.agent-assistant_incomplete')).toContainText('Streamed draft before Stop.');
    await expect(pane.locator('.agent-assistant')).toHaveCount(0);
    await agentSection(pane, 'Conversation');
    await expect(pane.getByRole('button', { name: 'Resume', exact: true })).toBeEnabled();
    await expect.poll(() => closed).toBe(true);
    const exported = JSON.parse(await api('export', { id }));
    expect(JSON.stringify(exported)).toContain('assistant_incomplete');
    expect(requests).toHaveLength(1);
  });
});
