import { providerEvents } from './agent-provider-fixtures.mjs';
import { agentSection } from './agent-fixture.mjs';
import { test, expect as baseExpect } from './studio-fixture.mjs';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const expect = baseExpect.configure({ timeout: 15000 });

for (const provider of ['openai', 'anthropic', 'gemini']) {
test(`workbench runs ${provider} official SDK tools, reviews the source change and restores it`, async ({ page, request, baseURL }) => {
  test.setTimeout(90000); page.setDefaultTimeout(15000);
  test.skip(!process.env.XAMLG_TEST_HOST_DLL, 'Build the companion and set XAMLG_TEST_HOST_DLL for the full agent transport test.');
  const requests = [], failures = [];
  const pageErrors = []; page.on('pageerror', error => pageErrors.push(error.message));
  let releaseFirst;
  const firstResponse = new Promise(resolve => { releaseFirst = resolve; });
  const moveResponse = Promise.withResolvers(), moveArrived = Promise.withResolvers();
  const composerDraftResponse = Promise.withResolvers();
  const xaml = '<TextBlock xmlns="https://github.com/avaloniaui" Text="Agent changed this" />';
  const report = 'Updated and compiled the real project.\n\n**Verified locally**\n\n```xml\n' + xaml + '\n```\n\n' +
    '[Project source](https://example.invalid/source)\n\n' +
    '<img src="https://example.invalid/embedded" onerror="window.agentMarkdownExecuted=true">\n\n' +
    '![Embedded image](https://example.invalid/image)\n\n[Unsafe link](javascript:alert(1))';
  const reviewBaseline = '<StackPanel xmlns="https://github.com/avaloniaui">\r\n' +
    '  <TextBlock Text="First 🦊 before" />\r\n  <Border Height="8" />\n' +
    '  <TextBlock Text="Second before" />\r\n</StackPanel>';
  const reviewAfter = reviewBaseline.replace('First 🦊 before', 'First 🦊 after').replace('Second before', 'Second after');
  const readSource = () => page.evaluate(() => window.xamlgAutomation.call('xamlg_document_read', { path: 'View.axaml' }));
  const writeSource = text => page.evaluate(async text => {
    const project = await window.xamlgAutomation.call('xamlg_project_get', {});
    return window.xamlgAutomation.call('xamlg_document_write', { path: 'View.axaml', text, expectedRevision: project.revision });
  }, text);
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
        if (round <= 5 && !result('call_3').success) throw new Error('The agent edit did not compile.');
        if (round > 5) {
          if (!JSON.stringify(input).includes('Create a concise public context checkpoint')) throw new Error('Expected the reviewed checkpoint request.');
          if (input.tools?.length) throw new Error('Compaction must not offer tools.');
        }
        output = [{ type: 'message', id: 'msg_done', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: round > 5 ? 'The project was changed and compiled. Source restoration was reviewed. Inspect the current workspace before further changes.' : report, annotations: [] }] }];
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
  const stateDirectory = await mkdtemp(join(tmpdir(), 'xamlg-workbench-state-'));
  const host = spawn(process.env.XAMLG_TEST_DOTNET || 'dotnet', [process.env.XAMLG_TEST_HOST_DLL, `--port=${companionPort}`, `--agent-store=${stateDirectory}`, '--chatgpt=false', ...originArgs], {
    env: environment, stdio: ['ignore', 'pipe', 'pipe']
  });
  let hostLog = ''; host.stdout.on('data', part => { hostLog += part; }); host.stderr.on('data', part => { hostLog += part; });
  try {
    await expect.poll(async () => {
      if (host.exitCode != null) throw new Error(hostLog);
      try { return (await request.get(`http://127.0.0.1:${companionPort}/health`)).ok(); } catch { return false; }
    }).toBe(true);
    await page.goto('./'); await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true', { timeout: 60000 });
    await page.getByTestId('agent-access').click();
    await page.getByLabel('Enable access to this live project').check();
    await page.getByLabel('Permission profile', { exact: true }).selectOption('FullAccess');
    await page.getByLabel('Companion WebSocket').fill(`ws://127.0.0.1:${companionPort}/bridge`);
    await page.getByLabel('Owner token').fill(token);
    await page.getByRole('button', { name: 'Connect companion' }).click();
    await expect(page.locator('.agent-access-panel').getByRole('status')).toContainText('Connected');
    await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
    const original = await page.evaluate(() => window.xamlgAutomation.call('xamlg_document_read', { path: 'View.axaml' }));
    await page.getByTestId('agent-workbench').click();
    const workbench = page.getByRole('region', { name: 'Coding agent workbench' });
    await agentSection(workbench, 'Connection');
    await workbench.getByLabel('Agent connection', { exact: true }).selectOption('companion');
    await expect(workbench.getByLabel('Provider', { exact: true })).toHaveValue(provider);
    await agentSection(workbench, 'Connection');
    await workbench.getByRole('button', { name: 'Discover models' }).click();
    await expect(workbench.locator('#agent-models option')).toHaveCount(1);
    await workbench.getByLabel('Model', { exact: true }).fill('test-model');
    await agentSection(workbench, 'Tasks');
    await workbench.getByLabel('Task name', { exact: true }).fill('Change and review');
    await workbench.getByRole('button', { name: 'Create task', exact: true }).click();
    await agentSection(workbench, 'Permissions');
    await workbench.getByLabel('Task permission profile').selectOption('autoEdit');
    await page.route('**/agent/draft', async route => {
      const response = await route.fetch();
      await composerDraftResponse.promise;
      await route.fulfill({ response });
    });
    const composer = workbench.getByLabel('Message', { exact: true });
    const prompt = 'Change the TextBlock, compile and report.';
    const review = page.getByRole('dialog', { name: 'Review agent run' });
    await agentSection(workbench, 'Conversation');
    await composer.fill(prompt);
    await expect(composer).toHaveAttribute('data-can-submit', 'true');
    await composer.press('Shift+Enter');
    await expect(composer).toHaveValue(prompt + '\n');
    await expect(review).not.toBeVisible();
    await agentSection(workbench, 'Conversation');
    await composer.fill(prompt);
    await composer.dispatchEvent('compositionstart', { data: '文字' });
    await composer.press('Enter');
    await expect(composer).toHaveValue(prompt + '\n');
    await expect(review).not.toBeVisible();
    await composer.dispatchEvent('compositionend', { data: '文字' });
    await agentSection(workbench, 'Conversation');
    await composer.fill(prompt);
    await composer.press('Control+Enter');
    await expect(review).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(review).not.toBeVisible();
    expect(requests).toHaveLength(0);
    await expect(composer).toHaveAttribute('data-can-submit', 'true');
    await composer.press('Enter');
    await expect(review).toContainText('Change the TextBlock, compile and report.');
    expect(requests).toHaveLength(0);
    composerDraftResponse.resolve();
    await review.getByRole('button', { name: 'Confirm run', exact: true }).click();
    await expect.poll(() => requests.length).toBe(1);
    await agentSection(workbench, 'Conversation');
    await workbench.getByLabel('Message', { exact: true }).fill('First queued follow-up');
    await workbench.getByRole('button', { name: 'Queue follow-up', exact: true }).click();
    await agentSection(workbench, 'Conversation');
    await expect(workbench.getByLabel('Message', { exact: true })).toHaveValue('');
    await agentSection(workbench, 'Conversation');
    await workbench.getByLabel('Message', { exact: true }).fill('Keep this message queued');
    await workbench.getByRole('button', { name: 'Queue follow-up', exact: true }).click();
    await expect(workbench.locator('.agent-queue summary')).toContainText('2 queued follow-ups');
    await page.route('**/agent/queue_move', async route => {
      const response = await route.fetch(); moveArrived.resolve();
      await moveResponse.promise; await route.fulfill({ response });
    });
    await agentSection(workbench, 'Queue');
    await workbench.getByRole('button', { name: 'Move down', exact: true }).click();
    await moveArrived.promise;
    await agentSection(workbench, 'Queue');
    await workbench.getByLabel('Edit queued message', { exact: true }).fill('Saved queued follow-up');
    await agentSection(workbench, 'Queue');
    await expect(workbench.getByRole('button', { name: 'Save queued edit', exact: true })).toBeDisabled();
    moveResponse.resolve();
    await agentSection(workbench, 'Queue');
    await workbench.getByRole('button', { name: 'Save queued edit', exact: true }).click();
    await agentSection(workbench, 'Queue');
    await expect(workbench.getByLabel('Queued message', { exact: true }).locator('option').last()).toHaveText('Saved queued follow-up');
    await agentSection(workbench, 'Queue');
    await expect(workbench.getByRole('button', { name: 'Send selected message', exact: true })).toBeDisabled();
    releaseFirst();
    await expect(workbench.locator('.agent-task-status')).toContainText('completed', { timeout: 30000 });
    expect(failures).toEqual([]); expect(requests).toHaveLength(4);
    await agentSection(workbench, 'Conversation');
    const rendered = workbench.locator('.agent-assistant .agent-markdown').filter({ hasText: 'Verified locally' });
    await expect(rendered.locator('strong')).toHaveText('Verified locally');
    await expect(rendered.locator('.agent-code-block code')).toHaveText(xaml);
    await expect(rendered.getByRole('link', { name: 'Project source' })).toHaveAttribute('href', 'https://example.invalid/source');
    await expect(rendered.locator('img, script, iframe, a[href^="javascript:"]')).toHaveCount(0);
    expect(await page.evaluate(() => window.agentMarkdownExecuted)).toBeUndefined();
    if (provider === 'openai') expect(requests.every(item => item.store === false)).toBe(true);
    expect((await page.evaluate(() => window.xamlgAutomation.call('xamlg_document_read', { path: 'View.axaml' }))).text).toBe(xaml);
    // The next accepted run captures this manual edit as its own source baseline.
    await writeSource(reviewBaseline);
    await expect(workbench.locator('.agent-queue summary')).toContainText('2 queued follow-ups');
    await agentSection(workbench, 'Conversation');
    await workbench.getByLabel('Message', { exact: true }).fill('Unsent independent composer draft');
    await agentSection(workbench, 'Permissions');
    await workbench.getByLabel('Task permission profile').selectOption('fullAccess');
    const taskId = await workbench.getByLabel('Task', { exact: true }).inputValue();
    const queuedId = await workbench.getByLabel('Queued message', { exact: true }).inputValue();
    await agentSection(workbench, 'Queue');
    await workbench.getByRole('button', { name: 'Send selected message', exact: true }).click();
    await expect(review).toContainText('Saved queued follow-up');
    await expect(review.getByRole('button', { name: 'Confirm run', exact: true })).toBeDisabled();
    await expect(page.evaluate(async ({ taskId, queuedId }) => {
      const studio = await window.xamlgBoot.importModule('studio.js');
      const state = await studio.agentRequest('state', {});
      const task = state.tasks.find(item => item.id === taskId);
      return studio.agentRequest('run', { id: taskId, message: null, options: { policy: { profile: 'fullAccess' } },
        confirmed: true, fullAccessAcknowledged: false, queuedMessageId: queuedId, expectedQueueRevision: task.queue.revision });
    }, { taskId, queuedId })).rejects.toThrow('Full Access acknowledgement');
    await review.getByRole('button', { name: 'Cancel', exact: true }).click();
    expect(requests).toHaveLength(4);
    await agentSection(workbench, 'Queue');
    await workbench.getByRole('button', { name: 'Send selected message', exact: true }).click();
    await page.evaluate(async ({ taskId, queuedId }) => {
      const studio = await window.xamlgBoot.importModule('studio.js');
      const state = await studio.agentRequest('state', {});
      const task = state.tasks.find(item => item.id === taskId);
      await studio.agentRequest('queue_edit', { id: taskId, messageId: queuedId, text: 'Reviewed after concurrent queue edit', expectedRevision: task.queue.revision });
    }, { taskId, queuedId });
    await review.getByRole('checkbox').check();
    await review.getByRole('button', { name: 'Confirm run', exact: true }).click();
    await expect(review.getByRole('alert')).toContainText('queue changed');
    expect(requests).toHaveLength(4);
    await review.getByRole('button', { name: 'Cancel', exact: true }).click();
    await workbench.getByRole('button', { name: 'Refresh coding agent', exact: true }).click();
    await agentSection(workbench, 'Queue');
    await expect(workbench.getByLabel('Queued message', { exact: true }).locator('option').last()).toHaveText('Reviewed after concurrent queue edit');
    await agentSection(workbench, 'Queue');
    await workbench.getByRole('button', { name: 'Send selected message', exact: true }).click();
    await expect(review).toContainText('Reviewed after concurrent queue edit');
    await expect(review.getByRole('checkbox')).not.toBeChecked();
    await review.getByRole('checkbox').check();
    await review.getByRole('button', { name: 'Confirm run', exact: true }).click();
    await expect.poll(() => requests.length).toBe(5);
    await expect(workbench.locator('.agent-task-status')).toContainText('completed');
    await agentSection(workbench, 'Conversation');
    await expect(workbench.getByLabel('Message', { exact: true })).toHaveValue('Unsent independent composer draft');
    await expect(workbench.locator('.agent-queue summary')).toContainText('1 queued follow-ups');
    const continuation = JSON.stringify(requests[4]);
    expect(continuation).toContain('Reviewed after concurrent queue edit');
    expect(continuation).not.toContain('Keep this message queued');
    expect(continuation).not.toContain('Unsent independent composer draft');
    await writeSource(reviewAfter);
    await agentSection(workbench, 'Changes');
    await workbench.getByLabel('Source comparison').selectOption('latest');
    await agentSection(workbench, 'Changes');
    await expect(workbench.getByLabel('Selected change block')).toHaveText('Change 1 of 2');
    await workbench.getByRole('button', { name: 'Open current document', exact: true }).click();
    await expect(page.locator('[data-tab-id="document:View.axaml"]')).toHaveAttribute('aria-selected', 'true');
    await agentSection(workbench, 'Changes');
    await workbench.getByRole('button', { name: 'Restore selected change', exact: true }).click();
    const restore = page.getByRole('dialog', { name: 'Review source restore' });
    await expect(restore).toContainText('First 🦊 after');
    await expect(restore).toContainText('First 🦊 before');
    await expect(restore).not.toContainText('Second after');
    await restore.getByRole('button', { name: 'Confirm source restore', exact: true }).click();
    await expect.poll(async () => (await readSource()).text).toBe(reviewAfter.replace('First 🦊 after', 'First 🦊 before'));
    // Restoration refreshes its review asynchronously. Wait for that capture
    // before Undo, so this assertion concerns a deliberately stale review.
    await expect(workbench.getByLabel('Selected change block')).toHaveText('Change 1 of 1');
    // Source restore is one ordinary project transaction, including its exact CRLF/LF.
    await page.evaluate(async () => {
      const project = await window.xamlgAutomation.call('xamlg_project_get', {});
      await window.xamlgAutomation.call('xamlg_project_undo', { expectedRevision: project.revision });
    });
    await expect.poll(async () => (await readSource()).text).toBe(reviewAfter);
    await agentSection(workbench, 'Changes');
    await expect(workbench.getByRole('button', { name: 'Restore selected change', exact: true })).toBeDisabled();
    await agentSection(workbench, 'Changes');
    await workbench.getByRole('button', { name: 'Refresh changes', exact: true }).click();
    await agentSection(workbench, 'Changes');
    await workbench.getByRole('button', { name: 'Next change', exact: true }).click();
    await agentSection(workbench, 'Changes');
    await expect(workbench.getByLabel('Selected change block')).toHaveText('Change 2 of 2');
    await agentSection(workbench, 'Changes');
    await workbench.getByRole('button', { name: 'Restore selected change', exact: true }).click();
    await expect(restore).toContainText('Second after');
    await writeSource(reviewAfter.replace('Height="8"', 'Height="9"'));
    await expect(restore.getByRole('button', { name: 'Confirm source restore', exact: true })).toBeDisabled();
    await restore.getByRole('button', { name: 'Cancel', exact: true }).click();
    expect((await readSource()).text).toContain('Second after');
    await agentSection(workbench, 'Changes');
    await workbench.getByLabel('Source comparison').selectOption('task');
    await agentSection(workbench, 'Changes');
    await workbench.getByRole('button', { name: 'Refresh changes', exact: true }).click();
    await expect(workbench.locator('details.agent-changes summary')).toHaveText('Review 1 changed source files');
    await workbench.getByLabel(/View.axaml \(/).check();
    await agentSection(workbench, 'Changes');
    await workbench.getByRole('button', { name: 'Show before and after' }).click();
    await expect(workbench.locator('details.agent-changes')).toContainText('Second after');
    await agentSection(workbench, 'Changes');
    await workbench.getByRole('button', { name: 'Restore selected files' }).click();
    await page.getByRole('dialog', { name: 'Review source restore' }).getByRole('button', { name: 'Confirm source restore', exact: true }).click();
    await expect.poll(async () => (await page.evaluate(() => window.xamlgAutomation.call('xamlg_document_read', { path: 'View.axaml' }))).text).toBe(original.text);
    await agentSection(workbench, 'Conversation');
    await workbench.getByRole('button', { name: 'Compact context' }).click();
    await expect(review).toContainText('Review context compaction');
    expect(requests).toHaveLength(5);
    await review.getByRole('checkbox').check();
    await review.getByRole('button', { name: 'Confirm compaction', exact: true }).click();
    await expect(workbench.locator('.agent-task-status')).toContainText('1 checkpoints');
    expect(failures).toEqual([]); expect(requests).toHaveLength(6);
    const downloadPromise = page.waitForEvent('download');
    await agentSection(workbench, 'Tasks');
    await workbench.getByRole('button', { name: 'Export thread' }).click();
    expect((await downloadPromise).suggestedFilename()).toBe('xamlg-agent-thread.json');
    await page.locator('[data-tab-id="agent"]').click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Float', exact: true }).click();
    await page.evaluate(async () => {
      const layout = await window.xamlgAutomation.call('xamlg_layout_get');
      await window.xamlgAutomation.call('xamlg_layout_set', { layout: layout.layout });
    });
    await page.getByTestId('agent-workbench').click();
    await expect(workbench.locator('.agent-task-status')).toContainText('1 checkpoints');
    expect(pageErrors).toEqual([]);
    await page.getByTestId('agent-access').click();
    await page.getByRole('button', { name: 'Revoke & disconnect' }).click();
  } finally {
    releaseFirst();
    moveResponse.resolve();
    composerDraftResponse.resolve();
    if (test.info().status !== test.info().expectedStatus) await test.info().attach('companion.log', { body: hostLog, contentType: 'text/plain' });
    host.kill('SIGTERM');
    await Promise.race([once(host, 'exit'), new Promise(resolve => setTimeout(resolve, 5000))]);
    if (host.exitCode === null && host.signalCode === null) { host.kill('SIGKILL'); await once(host, 'exit'); }
    fixture.closeAllConnections(); await new Promise(resolve => fixture.close(resolve));
    await rm(stateDirectory, { recursive: true, force: true });
  }
});
}
