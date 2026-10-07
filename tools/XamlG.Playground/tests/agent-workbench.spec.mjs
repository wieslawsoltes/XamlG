import { test, expect as baseExpect } from './studio-fixture.mjs';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';

const expect = baseExpect.configure({ timeout: 15000 });

for (const provider of ['openai', 'anthropic', 'gemini']) {
test(`workbench runs ${provider} official SDK tools, reviews the source change and restores it`, async ({ page, request, baseURL }) => {
  test.setTimeout(90000); page.setDefaultTimeout(15000);
  test.skip(!process.env.XAMLG_TEST_HOST_DLL, 'Build the companion and set XAMLG_TEST_HOST_DLL for the full agent transport test.');
  const requests = [], failures = [];
  let releaseFirst;
  const firstResponse = new Promise(resolve => { releaseFirst = resolve; });
  const moveResponse = Promise.withResolvers(), moveArrived = Promise.withResolvers();
  const xaml = '<TextBlock xmlns="https://github.com/avaloniaui" Text="Agent changed this" />';
  const fixture = createServer(async (incoming, response) => {
    try {
      let body = ''; for await (const part of incoming) body += part;
      if (incoming.method === 'GET') {
        const models = provider === 'openai' ? { object: 'list', data: [{ id: 'test-model', object: 'model', created: 1, owned_by: 'fixture' }] } :
          provider === 'anthropic' ? { data: [{ id: 'test-model', display_name: 'Test model', type: 'model', created_at: '2026-01-01T00:00:00Z' }], has_more: false, first_id: 'test-model', last_id: 'test-model' } :
          { models: [{ name: 'models/test-model', displayName: 'Test model', supportedGenerationMethods: ['generateContent'] }] };
        response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(models)); return;
      }
      const input = JSON.parse(body); requests.push(input);
      const round = requests.length;
      if (round === 1) await firstResponse;
      const call = (name, args) => ({ type: 'function_call', id: `fc_${round}`, call_id: `call_${round}`, name, arguments: JSON.stringify(args), status: 'completed' });
      const result = id => provider === 'openai' ? JSON.parse(input.input.find(item => item.type === 'function_call_output' && item.call_id === id).output) :
        provider === 'anthropic' ? JSON.parse(input.messages.flatMap(message => Array.isArray(message.content) ? message.content : []).find(item => item.type === 'tool_result' && item.tool_use_id === id).content) :
        input.contents.flatMap(message => message.parts).find(item => item.functionResponse?.id === id).functionResponse.response;
      let output;
      if (round === 1) output = [call('xamlg_project_get', {})];
      else if (round === 2) {
        output = [call('xamlg_document_write', { path: 'View.axaml', text: xaml, expectedRevision: result('call_1').revision })];
      } else if (round === 3) output = [call('xamlg_compiler_compile', {})];
      else {
        if (!result('call_3').success) throw new Error('The agent edit did not compile.');
        output = [{ type: 'message', id: 'msg_done', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'Updated and compiled the real project.', annotations: [] }] }];
      }
      response.setHeader('Content-Type', 'text/event-stream');
      response.end(providerEvents(provider, output, round).map(event => `data: ${JSON.stringify(event)}\n\n`).join(''));
    } catch (error) { failures.push(error.message); response.statusCode = 500; response.end('Fixture failed'); }
  });
  fixture.listen(0, '127.0.0.1'); await once(fixture, 'listening');
  const fixturePort = fixture.address().port;
  // Reserve a free port, then release it immediately before starting Kestrel.
  const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
  const companionPort = reservation.address().port; await new Promise(resolve => reservation.close(resolve));
  const token = 'xamlg-agent-browser-test-token-0123456789';
  const origin = new URL(baseURL).origin;
  const environment = { ...process.env, XAMLG_STUDIO_OWNER_TOKEN: token,
    XAMLG_STUDIO_TOKEN: 'xamlg-external-client-test-token-9876543210' };
  for (const name of ['OPENAI_API_KEY', 'ANTHROPIC_API_KEY', 'GEMINI_API_KEY', 'GOOGLE_API_KEY']) delete environment[name];
  environment[`${provider.toUpperCase()}_API_KEY`] = 'test-only-not-a-real-key';
  environment[`${provider.toUpperCase()}_ENDPOINT`] = `http://127.0.0.1:${fixturePort}${provider === 'openai' ? '/v1' : ''}`;
  const originArgs = origin === 'https://wieslawsoltes.github.io' ? [] : [`--origins=${origin}`];
  const host = spawn(process.env.XAMLG_TEST_DOTNET || 'dotnet', [process.env.XAMLG_TEST_HOST_DLL, `--port=${companionPort}`, ...originArgs], {
    env: environment, stdio: ['ignore', 'pipe', 'pipe']
  });
  let hostLog = ''; host.stdout.on('data', part => { hostLog += part; }); host.stderr.on('data', part => { hostLog += part; });
  try {
    await expect.poll(async () => {
      if (host.exitCode != null) throw new Error(hostLog);
      try { return (await request.get(`http://127.0.0.1:${companionPort}/health`)).ok(); } catch { return false; }
    }).toBe(true);
    await page.goto('./'); await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
    await page.getByTestId('agent-access').click();
    await page.getByLabel('Enable access to this live project').check();
    await page.getByLabel('Permission profile', { exact: true }).selectOption('FullAccess');
    await page.getByLabel('Companion WebSocket').fill(`ws://127.0.0.1:${companionPort}/bridge`);
    await page.getByLabel('Owner token').fill(token);
    await page.getByRole('button', { name: 'Connect companion' }).click();
    await expect(page.getByRole('dialog', { name: 'Agent access' }).getByRole('status')).toContainText('Connected');
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    const original = await page.evaluate(() => window.xamlgAutomation.call('xamlg_document_read', { path: 'View.axaml' }));
    await page.getByTestId('agent-workbench').click();
    const workbench = page.getByRole('region', { name: 'Coding agent workbench' });
    await expect(workbench.getByLabel('Provider', { exact: true })).toHaveValue(provider);
    await workbench.getByRole('button', { name: 'Discover models' }).click();
    await expect(workbench.locator('#agent-models option')).toHaveCount(1);
    await workbench.getByLabel('Model', { exact: true }).fill('test-model');
    await workbench.getByLabel('Task name', { exact: true }).fill('Change and review');
    await workbench.getByRole('button', { name: 'Create task', exact: true }).click();
    await workbench.getByText('Run permissions and limits', { exact: true }).click();
    await workbench.getByLabel('Task permission profile').selectOption('autoEdit');
    await workbench.getByLabel('Message', { exact: true }).fill('Change the TextBlock, compile and report.');
    await workbench.getByRole('button', { name: 'Run', exact: true }).click();
    const review = page.getByRole('dialog', { name: 'Review agent run' });
    await expect(review).toContainText('Change the TextBlock, compile and report.');
    expect(requests).toHaveLength(0);
    await review.getByRole('button', { name: 'Confirm run', exact: true }).click();
    await expect.poll(() => requests.length).toBe(1);
    await workbench.getByLabel('Message', { exact: true }).fill('First queued follow-up');
    await workbench.getByRole('button', { name: 'Queue follow-up', exact: true }).click();
    await expect(workbench.getByLabel('Message', { exact: true })).toHaveValue('');
    await workbench.getByLabel('Message', { exact: true }).fill('Keep this message queued');
    await workbench.getByRole('button', { name: 'Queue follow-up', exact: true }).click();
    await expect(workbench.locator('.agent-queue summary')).toContainText('2 queued follow-ups');
    await page.route('**/agent/queue_move', async route => {
      const response = await route.fetch(); moveArrived.resolve();
      await moveResponse.promise; await route.fulfill({ response });
    });
    await workbench.getByRole('button', { name: 'Move down', exact: true }).click();
    await moveArrived.promise;
    await workbench.getByLabel('Edit queued message', { exact: true }).fill('Saved queued follow-up');
    await expect(workbench.getByRole('button', { name: 'Save queued edit', exact: true })).toBeDisabled();
    moveResponse.resolve();
    await workbench.getByRole('button', { name: 'Save queued edit', exact: true }).click();
    await expect(workbench.getByLabel('Queued message', { exact: true }).locator('option').last()).toHaveText('Saved queued follow-up');
    await expect(workbench.getByRole('button', { name: 'Send selected message', exact: true })).toBeDisabled();
    releaseFirst();
    await expect(workbench.getByRole('status')).toContainText('completed', { timeout: 30000 });
    expect(failures).toEqual([]); expect(requests).toHaveLength(4);
    if (provider === 'openai') expect(requests.every(item => item.store === false)).toBe(true);
    expect((await page.evaluate(() => window.xamlgAutomation.call('xamlg_document_read', { path: 'View.axaml' }))).text).toBe(xaml);
    await expect(workbench.locator('.agent-queue summary')).toContainText('2 queued follow-ups');
    await workbench.getByLabel('Message', { exact: true }).fill('Unsent independent composer draft');
    await workbench.getByLabel('Task permission profile').selectOption('fullAccess');
    const taskId = await workbench.getByLabel('Task', { exact: true }).inputValue();
    const queuedId = await workbench.getByLabel('Queued message', { exact: true }).inputValue();
    await workbench.getByRole('button', { name: 'Send selected message', exact: true }).click();
    await expect(review).toContainText('Saved queued follow-up');
    await expect(review.getByRole('button', { name: 'Confirm run', exact: true })).toBeDisabled();
    await expect(page.evaluate(async ({ taskId, queuedId }) => {
      const studio = await import('./studio.js');
      const state = await studio.agentRequest('state', {});
      const task = state.tasks.find(item => item.id === taskId);
      return studio.agentRequest('run', { id: taskId, message: null, options: { policy: { profile: 'fullAccess' } },
        confirmed: true, fullAccessAcknowledged: false, queuedMessageId: queuedId, expectedQueueRevision: task.queue.revision });
    }, { taskId, queuedId })).rejects.toThrow('Full Access acknowledgement');
    await review.getByRole('button', { name: 'Cancel', exact: true }).click();
    expect(requests).toHaveLength(4);
    await workbench.getByRole('button', { name: 'Send selected message', exact: true }).click();
    await page.evaluate(async ({ taskId, queuedId }) => {
      const studio = await import('./studio.js');
      const state = await studio.agentRequest('state', {});
      const task = state.tasks.find(item => item.id === taskId);
      await studio.agentRequest('queue_edit', { id: taskId, messageId: queuedId, text: 'Reviewed after concurrent queue edit', expectedRevision: task.queue.revision });
    }, { taskId, queuedId });
    await review.getByRole('checkbox').check();
    await review.getByRole('button', { name: 'Confirm run', exact: true }).click();
    await expect(review.getByRole('alert')).toContainText('queue changed');
    expect(requests).toHaveLength(4);
    await review.getByRole('button', { name: 'Cancel', exact: true }).click();
    await workbench.getByRole('button', { name: 'Refresh', exact: true }).click();
    await expect(workbench.getByLabel('Queued message', { exact: true }).locator('option').last()).toHaveText('Reviewed after concurrent queue edit');
    await workbench.getByRole('button', { name: 'Send selected message', exact: true }).click();
    await expect(review).toContainText('Reviewed after concurrent queue edit');
    await expect(review.getByRole('checkbox')).not.toBeChecked();
    await review.getByRole('checkbox').check();
    await review.getByRole('button', { name: 'Confirm run', exact: true }).click();
    await expect.poll(() => requests.length).toBe(5);
    await expect(workbench.getByRole('status')).toContainText('completed');
    await expect(workbench.getByLabel('Message', { exact: true })).toHaveValue('Unsent independent composer draft');
    await expect(workbench.locator('.agent-queue summary')).toContainText('1 queued follow-ups');
    const continuation = JSON.stringify(requests[4]);
    expect(continuation).toContain('Reviewed after concurrent queue edit');
    expect(continuation).not.toContain('Keep this message queued');
    expect(continuation).not.toContain('Unsent independent composer draft');
    await workbench.getByText('Review 1 changed source files', { exact: true }).click();
    await workbench.getByLabel(/View.axaml \(/).check();
    await workbench.getByRole('button', { name: 'Show before and after' }).click();
    await expect(workbench.locator('details.agent-changes')).toContainText(xaml);
    await workbench.getByRole('button', { name: 'Restore selected files' }).click();
    await expect.poll(async () => (await page.evaluate(() => window.xamlgAutomation.call('xamlg_document_read', { path: 'View.axaml' }))).text).toBe(original.text);
    await workbench.getByRole('button', { name: 'Compact context' }).click();
    await expect(workbench.getByRole('status')).toContainText('1 checkpoints');
    const downloadPromise = page.waitForEvent('download');
    await workbench.getByRole('button', { name: 'Export thread' }).click();
    expect((await downloadPromise).suggestedFilename()).toBe('xamlg-agent-thread.json');
    await page.evaluate(async () => {
      const layout = await window.xamlgAutomation.call('xamlg_layout_get');
      await window.xamlgAutomation.call('xamlg_layout_content', { contentId: 'agent', operation: 'floatInPage' });
      await window.xamlgAutomation.call('xamlg_layout_set', { layout: layout.layout });
    });
    await page.getByTestId('agent-workbench').click();
    await expect(workbench.getByRole('status')).toContainText('1 checkpoints');
    await page.getByTestId('agent-access').click();
    await page.getByRole('button', { name: 'Revoke & disconnect' }).click();
  } finally {
    releaseFirst();
    moveResponse.resolve();
    if (test.info().status !== test.info().expectedStatus) await test.info().attach('companion.log', { body: hostLog, contentType: 'text/plain' });
    host.kill('SIGTERM');
    await Promise.race([once(host, 'exit'), new Promise(resolve => setTimeout(resolve, 5000))]);
    if (host.exitCode === null) host.kill('SIGKILL');
    fixture.closeAllConnections(); await new Promise(resolve => fixture.close(resolve));
  }
});
}

function providerEvents(provider, output, round) {
  if (provider === 'openai') return [{ type: 'response.completed', sequence_number: round,
    response: { id: `resp_${round}`, object: 'response', created_at: 123, model: 'test-model', status: 'completed', output, usage: { input_tokens: 10, output_tokens: 5, total_tokens: 15 } } }];
  if (provider === 'gemini') return [{ candidates: [{ index: 0, content: { role: 'model', parts: output.map(item => item.type === 'function_call' ?
    { functionCall: { id: item.call_id, name: item.name, args: JSON.parse(item.arguments) } } : { text: item.content[0].text }) }, finishReason: 'STOP' }],
    usageMetadata: { promptTokenCount: 10, candidatesTokenCount: 5, totalTokenCount: 15 } }];
  const events = [{ type: 'message_start', message: { id: `msg_${round}`, type: 'message', role: 'assistant', model: 'test-model', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 0 } } }];
  output.forEach((item, index) => {
    events.push({ type: 'content_block_start', index, content_block: item.type === 'function_call' ? { type: 'tool_use', id: item.call_id, name: item.name, input: {} } : { type: 'text', text: '' } });
    events.push({ type: 'content_block_delta', index, delta: item.type === 'function_call' ? { type: 'input_json_delta', partial_json: item.arguments } : { type: 'text_delta', text: item.content[0].text } });
    events.push({ type: 'content_block_stop', index });
  });
  events.push({ type: 'message_delta', delta: { stop_reason: output.some(item => item.type === 'function_call') ? 'tool_use' : 'end_turn', stop_sequence: null }, usage: { output_tokens: 5 } });
  events.push({ type: 'message_stop' });
  return events;
}
