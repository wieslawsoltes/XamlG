import { test, expect } from './studio-fixture.mjs';
import { withAgentWorkbench, agentReply, agentDelta, sendAgentEvent, createAgentTask, completedTask, agentSection } from './agent-fixture.mjs';

const call = (sequence, name, args) => [{ type: 'function_call', id: `call_${sequence}`, call_id: `call_${sequence}`,
  name, status: 'completed', arguments: JSON.stringify(args) }];

test('direct sending queues and steers live work, then Stop resumes the same thread', async ({ page, request, baseURL }) => {
  let finishFirst;
  await withAgentWorkbench({ page, request, baseURL }, ({ response, sequence }) => {
    if (sequence === 1) {
      sendAgentEvent(response, agentDelta('Inspecting the project…'));
      finishFirst = () => { sendAgentEvent(response, agentReply('First response', sequence + 1)); response.end(); };
    } else if (sequence === 4 || sequence === 6) {
      sendAgentEvent(response, agentDelta('Unfinished work before stopping.'));
    } else { sendAgentEvent(response, agentReply(`Finished turn ${sequence}`, sequence + 1)); response.end(); }
  }, async ({ pane, api, requests }) => {
    const id = await createAgentTask(pane, 'Send and resume', 'test-model', { directSend: true });
    const composer = pane.getByLabel('Message', { exact: true });
    await composer.fill('Inspect this project'); await composer.press('Enter');
    await expect(pane.getByRole('log')).toContainText('Inspecting the project');
    await expect(composer).toHaveValue('');
    await composer.fill('Run the follow-up after this turn');
    await pane.getByRole('button', { name: 'Queue message', exact: true }).click();
    await expect(pane.getByLabel('Queued messages')).toContainText('Run the follow-up');
    await composer.fill('Keep all changes in View.axaml');
    await pane.getByRole('button', { name: 'Steer now', exact: true }).click();
    await expect(pane.getByLabel('Queued messages')).toContainText('Steering');
    expect(requests).toHaveLength(1);
    finishFirst(); await completedTask(api, id);
    expect(requests).toHaveLength(3);
    expect(JSON.stringify(requests[1].input)).toContain('Keep all changes in View.axaml');
    expect(JSON.stringify(requests[1].input)).not.toContain('Run the follow-up');
    expect(JSON.stringify(requests[2].input)).toContain('Run the follow-up');
    await expect(pane.getByLabel('Queued messages')).toHaveCount(0);
    await composer.fill('Continue the investigation'); await composer.press('Enter');
    await expect(pane.getByRole('log')).toContainText('Unfinished work before stopping.');
    await composer.press('Escape');
    await expect(pane.locator('.agent-resume-banner')).toContainText('Stopped');
    await pane.getByRole('button', { name: 'Resume', exact: true }).click();
    await completedTask(api, id);
    expect(requests).toHaveLength(5);
    expect(JSON.stringify(requests[4].input)).not.toContain('Unfinished work before stopping.');
    expect((await api('state')).tasks).toHaveLength(1);
    await composer.fill('One more interrupted turn'); await composer.press('Enter');
    await expect.poll(() => requests.length).toBe(6);
    await expect(pane.locator('.agent-working')).toBeVisible();
    await composer.press('Escape');
    await expect(pane.getByRole('button', { name: 'Resume', exact: true })).toBeEnabled();
    await agentSection(pane, 'Permissions');
    await pane.getByLabel('Review every run before sending').check();
    await agentSection(pane, 'Conversation');
    await composer.fill('Finish with this reviewed follow-up');
    await pane.getByRole('button', { name: 'Send message', exact: true }).click();
    await page.getByRole('dialog', { name: 'Review agent run' }).getByRole('button', { name: 'Confirm run', exact: true }).click();
    await completedTask(api, id);
    expect(requests).toHaveLength(8);
    expect(JSON.stringify(requests[7].input)).toContain('Finish with this reviewed follow-up');
    await expect(pane.locator('.agent-composer')).toBeInViewport();
    await page.screenshot({ path: test.info().outputPath('agent-workflow.png'), fullPage: true });
  });
});

test('plan handoff and a persistent goal are usable from the conversation', async ({ page, request, baseURL }) => {
  await withAgentWorkbench({ page, request, baseURL }, ({ response, sequence }) => {
    const output = [2, 4].includes(sequence) ? call(sequence, 'xamlg_project_get', {}) : sequence === 6 ?
      call(sequence, 'xamlg_agent_goal', { status: 'complete', evidence: 'Inspected the current project and verified its document inventory.' }) : null;
    const text = sequence === 1 ? '# Implementation plan\n\n1. Inspect the project.\n2. Verify the document inventory.' : `Verified step ${sequence}`;
    sendAgentEvent(response, agentReply(text, sequence + 1, false, output)); response.end();
  }, async ({ pane, api, requests }) => {
    const id = await createAgentTask(pane, 'Plan and goal', 'test-model', { directSend: true });
    await pane.getByLabel('Collaboration mode').selectOption('plan');
    await pane.getByLabel('Message', { exact: true }).fill('Plan a project inventory');
    await pane.getByLabel('Message', { exact: true }).press('Enter');
    await completedTask(api, id);
    await expect(pane.locator('.agent-plan-ready')).toBeVisible();
    expect(JSON.stringify(requests[0].tools)).not.toContain('xamlg_source_edit');
    await pane.getByRole('button', { name: 'Implement plan', exact: true }).click();
    const review = page.getByRole('dialog', { name: 'Review agent run' });
    await expect(review).toContainText('Verify the document inventory');
    await review.getByRole('button', { name: 'Confirm run', exact: true }).click();
    await completedTask(api, id);
    await expect(pane.getByLabel('Collaboration mode')).toHaveValue('default');
    expect((await api('state')).tasks.find(task => task.id === id).proposedPlan.accepted).toBe(true);
    await pane.getByLabel('Message', { exact: true }).fill('/goal Inspect and verify every project document');
    await pane.getByLabel('Message', { exact: true }).press('Enter');
    await expect(pane.locator('.agent-goal-card')).toContainText('complete');
    await completedTask(api, id);
    const goal = (await api('state')).tasks.find(task => task.id === id).activeGoal;
    expect(goal.continuations).toBe(1); expect(goal.evidence).toContain('verified its document inventory');
    expect(requests).toHaveLength(7);
    await agentSection(pane, 'Plan');
    await expect(pane.locator('.agent-proposed-plan')).toContainText('Implementation plan');
    await expect(pane.getByRole('button', { name: 'Plan accepted', exact: true })).toBeDisabled();
  });
});
