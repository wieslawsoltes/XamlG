import { test, expect } from '@playwright/test';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';

test('workbench runs official SDK tools, reviews the source change and restores it', async ({ page, request }) => {
  test.setTimeout(90000); page.setDefaultTimeout(15000);
  test.skip(!process.env.XAMLG_TEST_HOST_DLL, 'Build the companion and set XAMLG_TEST_HOST_DLL for the full agent transport test.');
  const requests = [], failures = [];
  const xaml = '<TextBlock xmlns="https://github.com/avaloniaui" Text="Agent changed this" />';
  const fixture = createServer(async (incoming, response) => {
    try {
      let body = ''; for await (const part of incoming) body += part;
      if (incoming.method === 'GET') { response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify({ object: 'list', data: [{ id: 'test-model', object: 'model', created: 1, owned_by: 'fixture' }] })); return; }
      const input = JSON.parse(body); requests.push(input);
      const round = requests.length;
      const call = (name, args) => ({ type: 'function_call', id: `fc_${round}`, call_id: `call_${round}`, name, arguments: JSON.stringify(args), status: 'completed' });
      let output;
      if (round === 1) output = [call('xamlg_project_get', {})];
      else if (round === 2) {
        const result = input.input.find(item => item.type === 'function_call_output' && item.call_id === 'call_1');
        output = [call('xamlg_document_write', { path: 'View.axaml', text: xaml, expectedRevision: JSON.parse(result.output).revision })];
      } else if (round === 3) output = [call('xamlg_compiler_compile', {})];
      else {
        const result = input.input.find(item => item.type === 'function_call_output' && item.call_id === 'call_3');
        if (!JSON.parse(result.output).success) throw new Error('The agent edit did not compile.');
        output = [{ type: 'message', id: 'msg_done', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'Updated and compiled the real project.', annotations: [] }] }];
      }
      const event = { type: 'response.completed', sequence_number: round, response: { id: `resp_${round}`, object: 'response', created_at: 123, model: 'test-model', status: 'completed', output, usage: { input_tokens: 10, output_tokens: 5, total_tokens: 15 } } };
      response.setHeader('Content-Type', 'text/event-stream');
      response.end(`event: response.completed\ndata: ${JSON.stringify(event)}\n\n`);
    } catch (error) { failures.push(error.message); response.statusCode = 500; response.end('Fixture failed'); }
  });
  fixture.listen(0, '127.0.0.1'); await once(fixture, 'listening');
  const fixturePort = fixture.address().port;
  // Reserve a free port, then release it immediately before starting Kestrel.
  const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
  const companionPort = reservation.address().port; await new Promise(resolve => reservation.close(resolve));
  const token = 'xamlg-agent-browser-test-token-0123456789';
  const origin = new URL(process.env.PLAYGROUND_URL || 'http://127.0.0.1:8765/').origin;
  const host = spawn(process.env.XAMLG_TEST_DOTNET || 'dotnet', [process.env.XAMLG_TEST_HOST_DLL, `--port=${companionPort}`, `--origins=${origin}`], {
    env: { ...process.env, XAMLG_STUDIO_TOKEN: token, OPENAI_API_KEY: 'test-only-not-a-real-key', OPENAI_ENDPOINT: `http://127.0.0.1:${fixturePort}/v1` }, stdio: ['ignore', 'pipe', 'pipe']
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
    await page.getByLabel('Local access token').fill(token);
    await page.getByRole('button', { name: 'Connect companion' }).click();
    await expect(page.getByRole('dialog', { name: 'Agent access' }).getByRole('status')).toContainText('Connected');
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    const original = await page.evaluate(() => window.xamlgAutomation.call('xamlg_document_read', { path: 'View.axaml' }));
    await page.getByTestId('agent-workbench').click();
    const workbench = page.getByRole('region', { name: 'Coding agent workbench' });
    await expect(workbench.getByLabel('Provider', { exact: true })).toHaveValue('openai');
    await workbench.getByLabel('Model', { exact: true }).fill('test-model');
    await workbench.getByLabel('Task name', { exact: true }).fill('Change and review');
    await workbench.getByRole('button', { name: 'Create task', exact: true }).click();
    await workbench.getByText('Run permissions and limits', { exact: true }).click();
    await workbench.getByLabel('Task permission profile').selectOption('autoEdit');
    await workbench.getByLabel('Message', { exact: true }).fill('Change the TextBlock, compile and report.');
    await workbench.getByRole('button', { name: 'Run', exact: true }).click();
    await expect(workbench.getByRole('status')).toContainText('completed', { timeout: 30000 });
    expect(failures).toEqual([]); expect(requests).toHaveLength(4);
    expect(requests.every(item => item.store === false)).toBe(true);
    expect((await page.evaluate(() => window.xamlgAutomation.call('xamlg_document_read', { path: 'View.axaml' }))).text).toBe(xaml);
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
    if (test.info().status !== test.info().expectedStatus) await test.info().attach('companion.log', { body: hostLog, contentType: 'text/plain' });
    host.kill('SIGTERM');
    await Promise.race([once(host, 'exit'), new Promise(resolve => setTimeout(resolve, 5000))]);
    if (host.exitCode === null) host.kill('SIGKILL');
    fixture.closeAllConnections(); await new Promise(resolve => fixture.close(resolve));
  }
});
