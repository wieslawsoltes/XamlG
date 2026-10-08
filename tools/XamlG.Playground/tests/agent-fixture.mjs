import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { randomBytes } from 'node:crypto';
import { test, expect as baseExpect } from './studio-fixture.mjs';
import { openStudio } from './live-preview.mjs';

const expect = baseExpect.configure({ timeout: 15000 });
export const agentRequest = (page, action, args = {}) => page.evaluate(async ({ action, args }) =>
  (await import('./studio.js')).agentRequest(action, args), { action, args });

export function sendAgentEvent(response, event) {
  if (!response.headersSent) response.setHeader('Content-Type', 'text/event-stream');
  response.write(`data: ${JSON.stringify(event)}\n\n`);
}
export function agentReply(text, sequence, incomplete = false, output = null) {
  return { type: incomplete ? 'response.incomplete' : 'response.completed', sequence_number: sequence,
    response: { id: `response_${sequence}`, object: 'response', created_at: 123, model: 'test-model',
      status: incomplete ? 'incomplete' : 'completed', ...(incomplete ? { incomplete_details: { reason: 'max_output_tokens' } } : {}),
      output: output ?? [{ type: 'message', id: `message_${sequence}`, role: 'assistant', status: incomplete ? 'incomplete' : 'completed',
        content: [{ type: 'output_text', text, annotations: [] }] }], usage: { input_tokens: 10, output_tokens: 5, total_tokens: 15 } } };
}
export function agentDelta(text, sequence = 1) {
  return { type: 'response.output_text.delta', sequence_number: sequence, item_id: 'streamed_message', output_index: 0, content_index: 0, delta: text, logprobs: [] };
}

// A real companion and official OpenAI SDK, with deterministic loopback inference.
// Every instance excludes real provider keys and disables the developer's account store.
export async function withAgentWorkbench({ page, request, baseURL }, handle, execute) {
  if (!process.env.XAMLG_TEST_HOST_DLL) throw new Error('Run through scripts/test-browser-studio.py.');
  page.setDefaultTimeout(15000);
  const requests = [], failures = [], errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const fixture = createServer(async (incoming, response) => {
    try {
      if (incoming.method === 'GET') {
        response.setHeader('Content-Type', 'application/json');
        response.end(JSON.stringify({ object: 'list', data: [{ id: 'test-model', object: 'model', created: 1, owned_by: 'fixture' }] }));
        return;
      }
      let body = ''; for await (const part of incoming) body += part;
      const input = JSON.parse(body); requests.push(input);
      await handle({ input, response, sequence: requests.length, requests });
    } catch (error) {
      failures.push(error.message);
      if (!response.headersSent) response.statusCode = 500;
      response.end();
    }
  });
  fixture.listen(0, '127.0.0.1'); await once(fixture, 'listening');
  const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
  const port = reservation.address().port; await new Promise(resolve => reservation.close(resolve));
  const token = randomBytes(32).toString('hex');
  const environment = { ...process.env, XAMLG_STUDIO_OWNER_TOKEN: token, XAMLG_STUDIO_TOKEN: randomBytes(32).toString('hex') };
  for (const name of ['OPENAI_API_KEY', 'ANTHROPIC_API_KEY', 'GEMINI_API_KEY', 'GOOGLE_API_KEY']) delete environment[name];
  environment.OPENAI_API_KEY = 'test-only-not-a-real-key';
  environment.OPENAI_ENDPOINT = `http://127.0.0.1:${fixture.address().port}/v1`;
  const origin = new URL(baseURL).origin;
  const host = spawn(process.env.XAMLG_TEST_DOTNET || 'dotnet', [process.env.XAMLG_TEST_HOST_DLL,
    `--port=${port}`, '--chatgpt=false', ...(origin === 'https://wieslawsoltes.github.io' ? [] : [`--origins=${origin}`])],
    { env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
  let log = '';
  host.stdout.on('data', data => { log += data; }); host.stderr.on('data', data => { log += data; });
  try {
    await expect.poll(async () => {
      if (host.exitCode != null) throw new Error(log);
      try { const result = await request.get(`http://127.0.0.1:${port}/health`); try { return result.ok(); } finally { await result.dispose(); } }
      catch { return false; }
    }).toBe(true);
    await openStudio(page);
    await page.getByTestId('agent-access').click();
    await page.getByLabel('Companion WebSocket').fill(`ws://127.0.0.1:${port}/bridge`);
    await page.getByLabel('Owner token').fill(token);
    await page.getByRole('button', { name: 'Connect companion', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Agent access' }).getByRole('status')).toContainText('Connected');
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByTestId('agent-workbench').click();
    const pane = page.getByRole('region', { name: 'Coding agent workbench' });
    await expect(pane.getByLabel('Provider', { exact: true })).toHaveValue('openai');
    await execute({ page, pane, requests, api: (action, args) => agentRequest(page, action, args) });
    expect(failures).toEqual([]); expect(errors).toEqual([]);
  } catch (error) {
    await test.info().attach('workbench-before-cleanup', { body: await page.getByRole('region', { name: 'Coding agent workbench' }).innerText().catch(() => 'Unavailable'), contentType: 'text/plain' });
    await test.info().attach('companion.log', { body: log.replaceAll(token, '[fixture owner]').replaceAll(environment.XAMLG_STUDIO_TOKEN, '[fixture client]'), contentType: 'text/plain' });
    throw error;
  } finally {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
    host.kill('SIGTERM');
    await Promise.race([once(host, 'exit'), new Promise(resolve => setTimeout(resolve, 5000))]);
    if (host.exitCode === null) { host.kill('SIGKILL'); await once(host, 'exit'); }
    fixture.closeAllConnections(); await new Promise(resolve => fixture.close(resolve));
  }
}

export async function createAgentTask(pane, name) {
  const create = pane.locator('details.agent-create');
  if (await create.getAttribute('open') === null) await create.locator('summary').click();
  await create.getByLabel('Model', { exact: true }).fill('test-model');
  await create.getByLabel('Task name', { exact: true }).fill(name);
  await create.getByRole('button', { name: 'Create task', exact: true }).click();
  await expect(pane.getByLabel('Rename task', { exact: true })).toHaveValue(name);
  return pane.getByLabel('Task', { exact: true }).inputValue();
}

export async function reviewAgentRun(page, pane, text) {
  await pane.getByLabel('Message', { exact: true }).fill(text);
  await pane.getByRole('button', { name: 'Run', exact: true }).click();
  const review = page.getByRole('dialog', { name: 'Review agent run' });
  await expect(review).toContainText(text);
  await review.getByRole('button', { name: 'Confirm run', exact: true }).click();
}

export async function completedTask(api, id) {
  await expect.poll(async () => {
    const task = (await api('state')).tasks.find(task => task.id === id);
    return task.status === 'completed' && task.events.at(-1)?.kind === 'changes';
  }).toBe(true);
}
