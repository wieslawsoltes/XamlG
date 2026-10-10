import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { randomBytes } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test, expect as baseExpect } from './studio-fixture.mjs';
import { openStudio } from './live-preview.mjs';
import { providerEvents, modelCatalog } from './agent-provider-fixtures.mjs';

const expect = baseExpect.configure({ timeout: 15000 });
const section = (pane, name) => pane.getByRole('navigation', { name: 'Agent sections' }).getByRole('button', { name, exact: true }).click();
for (const provider of ['openai', 'anthropic', 'gemini']) {
test(`local ${provider} relay keeps provider credentials on the server and needs no MCP pairing`, async ({ page, request, baseURL }) => {
  test.setTimeout(60000);
  test.skip(!process.env.XAMLG_TEST_HOST_DLL, 'Run through scripts/test-browser-studio.py.');
  const owner = randomBytes(32).toString('hex'), client = randomBytes(32).toString('hex'), key = 'synthetic-server-key-no-billing';
  const requests = [], failures = [], browserRequests = [];
  const held = Promise.withResolvers(); let holdModels = false, modelsWaiting = 0;
  page.on('request', request => { if (request.url().includes('/provider/')) browserRequests.push({ url: request.url(), headers: request.headers(), body: request.postData() }); });
  const fixture = createServer(async (incoming, response) => {
    try {
      const credential = incoming.headers[provider === 'openai' ? 'authorization' : provider === 'anthropic' ? 'x-api-key' : 'x-goog-api-key'];
      expect(credential).toBe(provider === 'openai' ? `Bearer ${key}` : key);
      if (incoming.method === 'GET') {
        if (holdModels) { modelsWaiting++; await held.promise; }
        response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(modelCatalog(provider))); return;
      }
      let body = ''; for await (const part of incoming) body += part;
      requests.push(JSON.parse(body));
      const output = [{ type: 'message', id: 'msg_done', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'Local relay response.', annotations: [] }] }];
      response.setHeader('Content-Type', 'text/event-stream');
      response.end(providerEvents(provider, output, requests.length).map(event => `data: ${JSON.stringify(event)}\n\n`).join(''));
    } catch (error) { failures.push(error.message); response.statusCode = 500; response.end('Fixture failure'); }
  });
  fixture.listen(0, '127.0.0.1'); await once(fixture, 'listening');
  const reserve = createServer(); reserve.listen(0, '127.0.0.1'); await once(reserve, 'listening');
  const port = reserve.address().port; await new Promise(resolve => reserve.close(resolve));
  const origin = new URL(baseURL).origin, relay = `http://127.0.0.1:${port}`;
  const env = { ...process.env, XAMLG_STUDIO_OWNER_TOKEN: owner, XAMLG_STUDIO_TOKEN: client };
  for (const name of ['OPENAI_API_KEY', 'ANTHROPIC_API_KEY', 'GEMINI_API_KEY', 'GOOGLE_API_KEY']) delete env[name];
  env[`${provider.toUpperCase()}_API_KEY`] = key;
  env[`${provider.toUpperCase()}_ENDPOINT`] = `http://127.0.0.1:${fixture.address().port}${provider === 'openai' ? '/v1' : ''}`;
  const stateDirectory = await mkdtemp(join(tmpdir(), 'xamlg-relay-state-'));
  const host = spawn(process.env.XAMLG_TEST_DOTNET || 'dotnet', [process.env.XAMLG_TEST_HOST_DLL, `--port=${port}`, `--agent-store=${stateDirectory}`, '--chatgpt=false', `--origins=${origin}`], { env, stdio: ['ignore', 'pipe', 'pipe'] });
  let log = ''; host.stdout.on('data', data => { log += data; }); host.stderr.on('data', data => { log += data; });
  try {
    await expect.poll(async () => {
      if (host.exitCode != null) throw new Error('Relay exited unexpectedly.');
      try { const response = await request.get(relay + '/health'); const ready = response.ok(); await response.dispose(); return ready; } catch { return false; }
    }).toBe(true);
    for (const [headers, status] of [[{}, 401], [{ Authorization: `Bearer ${client}` }, 401], [{ Authorization: `Bearer ${owner}`, Origin: 'https://untrusted.invalid' }, 403]]) {
      const response = await request.get(relay + '/provider', { headers }); expect(response.status()).toBe(status); await response.dispose();
    }
    for (const target of ['https://untrusted.invalid/', '/v1/models?key=forbidden', '//untrusted.invalid', '/v1/files']) {
      const response = await request.get(relay + `/provider/${provider}`, { headers: { Authorization: `Bearer ${owner}` }, params: { target } });
      expect(response.status()).toBe(400); await response.dispose();
    }
    await openStudio(page, false); await page.getByTestId('agent-workbench').click();
    const pane = page.getByRole('region', { name: 'Coding agent workbench' });
    await section(pane, 'Connection');
    await pane.getByLabel('Agent connection', { exact: true }).selectOption('relay');
    await pane.getByLabel('Provider', { exact: true }).selectOption(provider);
    await pane.getByLabel('Relay origin', { exact: true }).fill(relay);
    await pane.getByLabel('Relay Owner token', { exact: true }).fill(owner);
    await pane.getByRole('button', { name: 'Discover models', exact: true }).click();
    await expect(pane.locator('#agent-models option')).toHaveCount(1);
    await pane.getByLabel('Model', { exact: true }).fill('test-model');
    await section(pane, 'Tasks'); await pane.getByRole('button', { name: 'Create task', exact: true }).click();
    await pane.getByLabel('Message', { exact: true }).fill('Reply through the local relay.');
    await pane.getByRole('button', { name: 'Send message', exact: true }).click();
    await expect(pane.locator('.agent-task-status')).toContainText('completed');
    await expect(pane.locator('.agent-thread')).toContainText('Local relay response.');
    expect(requests).toHaveLength(1); expect(failures).toEqual([]);
    expect(browserRequests).toHaveLength(2); expect(JSON.stringify(browserRequests)).not.toContain(key);
    expect(browserRequests.every(request => request.headers.authorization === `Bearer ${owner}`)).toBe(true);
    const health = await request.get(relay + '/health'); expect((await health.json()).connected).toBe(false); await health.dispose();
    await page.getByTestId('agent-access').click(); await expect(page.getByLabel('Enable access to this live project')).not.toBeChecked();
    if (provider === 'openai') {
      holdModels = true;
      const getModels = () => request.get(relay + '/provider/openai', { headers: { Authorization: `Bearer ${owner}` }, params: { target: '/v1/models' } });
      const active = Array.from({ length: 4 }, getModels);
      await expect.poll(() => modelsWaiting).toBe(4);
      const busy = await getModels(); expect(busy.status()).toBe(429);
      expect((await busy.json()).error.code).toBe('provider_busy'); await busy.dispose();
      held.resolve();
      for (const response of await Promise.all(active)) { expect(response.ok()).toBe(true); await response.dispose(); }
      const recovered = await getModels(); expect(recovered.ok()).toBe(true); await recovered.dispose();
    }
  } finally {
    held.resolve();
    host.kill('SIGTERM'); await Promise.race([once(host, 'exit'), new Promise(resolve => setTimeout(resolve, 5000))]);
    if (host.exitCode === null && host.signalCode === null) { host.kill('SIGKILL'); await once(host, 'exit'); }
    fixture.closeAllConnections(); await new Promise(resolve => fixture.close(resolve));
    await rm(stateDirectory, { recursive: true, force: true });
    if (test.info().status !== test.info().expectedStatus) await test.info().attach('relay.log', { body: log.replaceAll(owner, '[owner]').replaceAll(client, '[client]').replaceAll(key, '[key]'), contentType: 'text/plain' });
  }
});
}
