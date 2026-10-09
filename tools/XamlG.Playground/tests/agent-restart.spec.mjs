import { test, expect } from './studio-fixture.mjs';
import { withAgentWorkbench, sendAgentEvent, agentReply, createAgentTask, reviewAgentRun, completedTask, agentSection } from './agent-fixture.mjs';

test('companion restart restores the same project task and its native continuation without running automatically', async ({ page, request, baseURL }) => {
  await withAgentWorkbench({ page, request, baseURL }, ({ input, response, sequence }) => {
    if (sequence === 2) expect(JSON.stringify(input.input)).toContain('synthetic-encrypted-continuation');
    const reply = agentReply(sequence === 1 ? 'Before restart' : 'Continued after restart', sequence);
    if (sequence === 1) reply.response.output.unshift({ type: 'reasoning', id: 'reasoning_restart', summary: [], encrypted_content: 'synthetic-encrypted-continuation' });
    sendAgentEvent(response, reply); response.end();
  }, async ({ page, pane, requests, restartHost, api }) => {
    const id = await createAgentTask(pane, 'Durable companion');
    await reviewAgentRun(page, pane, 'Remember this conversation'); await completedTask(api, id);
    await api('queue', { id, text: 'Saved follow-up' });
    await agentSection(pane, 'Conversation');
    await pane.getByLabel('Message', { exact: true }).fill('Saved companion draft');
    await expect.poll(() => page.evaluate(async () => Object.values((await (await xamlgBoot.importModule('studio.js')).loadStudioState('agent-ui'))?.drafts || {}).includes('Saved companion draft'))).toBe(true);
    await restartHost();
    await page.reload();
    await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
    await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
    await page.getByTestId('agent-workbench').click();
    await expect(pane.getByLabel('Task', { exact: true })).toHaveValue(id);
    await expect(pane.locator('.agent-thread')).toContainText('Before restart');
    await expect(pane.getByLabel('Message', { exact: true })).toHaveValue('Saved companion draft');
    const restored = (await api('state')).tasks.find(task => task.id === id);
    expect(restored.isPreviousWorkspace).toBe(false); expect(restored.queue.messages[0].text).toBe('Saved follow-up');
    expect(restored.totalTokens).toBe(15); expect(requests).toHaveLength(1);
    await page.getByTestId('agent-access').click();
    await expect(page.getByLabel('Enable access to this live project')).not.toBeChecked();
    await page.getByLabel('Enable access to this live project').check();
    await page.getByLabel('Permission profile', { exact: true }).selectOption('FullAccess');
    await page.getByTestId('agent-workbench').click();
    await reviewAgentRun(page, pane, 'Continue with your saved conversation'); await completedTask(api, id);
    expect(requests).toHaveLength(2);
    await expect(pane.locator('.agent-thread')).toContainText('Continued after restart');
  });
});
