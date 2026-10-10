import { generateKeyPairSync, sign, createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { test, expect as baseExpect } from './studio-fixture.mjs';
import { withAgentWorkbench, createAgentTask, completedTask, agentReply, agentDelta, sendAgentEvent, agentSection } from './agent-fixture.mjs';

const expect = baseExpect.configure({ timeout: 15000 });
test.setTimeout(120000);

function accountServer() {
  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const key = { ...publicKey.export({ format: 'jwk' }), kid: 'browser-fixture', use: 'sig', alg: 'RS256' };
  const codes = new Map(); let registrations = 0, tokens = 0;
  const state = { authorizations: [], exchanges: [], inferenceTokens: [], revocations: [], plan: true, failToken: false, hold: false, revocationStatus: 200 };
  const json = (response, value, status = 200) => { response.statusCode = status; response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(value)); };
  const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
  return {
    state,
    async handleHttp(incoming, response, origin) {
      const url = new URL(incoming.url, origin);
      if (url.pathname === '/.well-known/openid-configuration') {
        json(response, { issuer: origin, jwks_uri: origin + '/keys', revocation_endpoint: origin + '/revoke' }); return true;
      }
      if (url.pathname === '/keys') { json(response, { keys: [key] }); return true; }
      if (url.pathname === '/api/accounts/authorize') {
        const query = Object.fromEntries(url.searchParams); state.authorizations.push(query);
        const client = query.client_id === 'dynamic_agent_client' ? `oaiapp_browser_${++registrations}` : query.client_id;
        const code = `code_${state.authorizations.length}`; codes.set(code, { query, client });
        const callback = new URL(query.redirect_uri); callback.search = new URLSearchParams({ state: query.state, code, client_id: client });
        response.setHeader('Content-Type', 'text/html');
        response.end(`<!doctype html><html><title>Synthetic account fixture</title><h1>Synthetic account fixture</h1><a href="${callback.href.replaceAll('&', '&amp;')}">Approve fixture sign-in</a></html>`);
        return true;
      }
      if (url.pathname === '/api/accounts/oauth/token' || url.pathname === '/revoke') {
        let body = ''; for await (const part of incoming) body += part;
        const fields = Object.fromEntries(new URLSearchParams(body));
        if (url.pathname === '/revoke') { state.revocations.push(fields); json(response, {}, state.revocationStatus); return true; }
        state.exchanges.push(fields);
        if (state.failToken) { state.failToken = false; json(response, { error: 'temporarily_unavailable' }, 503); return true; }
        const authorization = codes.get(fields.code); codes.delete(fields.code);
        if (!authorization || authorization.client !== fields.client_id || authorization.query.redirect_uri !== fields.redirect_uri ||
          createHash('sha256').update(fields.code_verifier).digest('base64url') !== authorization.query.code_challenge) {
          json(response, { error: 'invalid_grant' }, 400); return true;
        }
        const now = Math.floor(Date.now() / 1000);
        const payload = `${encode({ alg: 'RS256', kid: key.kid })}.${encode({ iss: origin, sub: 'subject:' + fields.client_id,
          aud: fields.client_id, iat: now, exp: now + 3600, nonce: authorization.query.nonce, email: 'browser@example.test' })}`;
        const identity = payload + '.' + sign('RSA-SHA256', Buffer.from(payload), privateKey).toString('base64url');
        tokens++;
        json(response, { token_type: 'Bearer', access_token: `synthetic_browser_access_${tokens}`, refresh_token: `synthetic_browser_refresh_${tokens}`,
          expires_in: 3600, id_token: identity, scope: 'openid profile email offline_access resource.invoke' + (state.plan ? ' chatgpt.tokens.use.direct' : '') });
        return true;
      }
      if (url.pathname === '/v1/models') {
        json(response, { models: [{ slug: 'fixture-z', display_name: 'First account model', visibility: 'list' },
          { slug: 'hidden', display_name: 'Hidden account model', visibility: 'hide' }, { slug: 'fixture-a', display_name: 'Second account model', visibility: 'list' }] });
        return true;
      }
      if (url.pathname === '/v1/responses') { state.inferenceTokens.push(incoming.headers.authorization); return false; }
      throw new Error('Unexpected account fixture route: ' + url.pathname);
    }
  };
}

async function accounts(pane) {
  await agentSection(pane, 'Connection');
  const section = pane.locator('details.agent-accounts');
  if (await section.getAttribute('open') === null) await section.locator('summary').first().click();
  return section;
}
async function popupSignIn(page, button, approve = true) {
  const opened = page.waitForEvent('popup'); await button.click(); const popup = await opened;
  await expect(popup.getByRole('heading', { name: 'Synthetic account fixture' })).toBeVisible();
  if (approve) { await popup.getByRole('link', { name: 'Approve fixture sign-in' }).click(); await expect(popup.locator('body')).toContainText('Return to XamlG Studio'); await popup.close(); }
  return popup;
}
async function addAccount(page, pane, label) {
  const section = await accounts(pane);
  const adding = section.locator('details').filter({ has: page.locator('summary', { hasText: /^(Sign in|Add another account or workspace)$/ }) });
  if (await adding.getAttribute('open') === null) await adding.locator('summary').click();
  await adding.getByLabel('New account label').fill(label);
  return popupSignIn(page, adding.getByRole('button', { name: 'Continue with ChatGPT', exact: true }));
}
async function signedIn(api, pane) {
  let state;
  await expect.poll(async () => (state = await api('state')).chatGpt.signIn.status).toBe('completed');
  await expect(pane.locator('details.agent-accounts').getByText('Sign-in completed.', { exact: true })).toHaveCount(1);
  return state.chatGpt.accounts.find(account => account.id === state.chatGpt.activeAccountId);
}

test('ChatGPT account UI signs in without an API key and keeps tasks bound while accounts and preferences change', async ({ page, request, baseURL }) => {
  const directory = await mkdtemp(join(tmpdir(), 'xamlg-account-browser-'));
  const fixture = accountServer();
  try {
    await withAgentWorkbench({ page, request, baseURL }, async ({ response, sequence }) => {
      sendAgentEvent(response, agentReply('Account task completed.', sequence)); response.end();
    }, async ({ pane, api, requests }) => {
      await agentSection(pane, 'Tasks');
      await expect(pane.getByRole('button', { name: 'Create task', exact: true })).toBeDisabled();
      await addAccount(page, pane, 'Alpha'); const alpha = await signedIn(api, pane);
      let section = await accounts(pane);
      await agentSection(pane, 'Connection');
      await section.getByLabel('Account label', { exact: true }).fill('Alpha saved');
      await agentSection(pane, 'Connection');
      await section.getByLabel('Remember credentials on this computer').first().check();
      await agentSection(pane, 'Connection');
      await section.getByRole('button', { name: 'Save account preferences' }).click();
      await expect.poll(async () => (await api('state')).chatGpt.accounts[0].remember).toBe(true);
      await agentSection(pane, 'Connection');
      await pane.getByRole('button', { name: 'Discover models', exact: true }).click();
      await expect.poll(() => pane.locator('#agent-models option').evaluateAll(options => options.map(option => option.value))).toEqual(['fixture-z', 'fixture-a']);
      const task = await createAgentTask(pane, 'Alpha task', 'fixture-z');
      await addAccount(page, pane, 'Beta'); const beta = await signedIn(api, pane);
      expect(beta.id).not.toBe(alpha.id); expect(beta.remember).toBe(true);
      expect((await api('state')).tasks.find(item => item.id === task).account.id).toBe(alpha.id);
      await agentSection(pane, 'Conversation');
      await pane.getByLabel('Message', { exact: true }).fill('Use the original account.');
      await agentSection(pane, 'Conversation');
      await pane.getByRole('button', { name: 'Send message', exact: true }).click();
      const review = page.getByRole('dialog', { name: 'Review agent run' });
      await expect(review).toContainText('Alpha saved'); await expect(review).not.toContainText('Beta');
      await expect(review).toContainText('local estimate');
      await review.getByRole('button', { name: 'Confirm run', exact: true }).click(); await completedTask(api, task);
      expect(fixture.state.inferenceTokens).toEqual(['Bearer synthetic_browser_access_1']);
      expect(requests[0].max_output_tokens).toBeUndefined(); expect(requests[0].tools[0].type).toBe('namespace');
      expect(requests[0].tools[0].name).toBe('xamlg');
      if (process.platform !== 'win32') {
        const stored = JSON.parse(await readFile(join(directory, 'accounts.dat'), 'utf8'));
        expect(stored.accounts.find(account => account.id === alpha.id).tokens.accessToken).toBe('synthetic_browser_access_1');
        expect(stored.accounts.find(account => account.id === beta.id).tokens.accessToken).toBe('synthetic_browser_access_2');
        expect((await stat(join(directory, 'accounts.dat'))).mode & 0o777).toBe(0o600);
      }
      const publicState = JSON.stringify(await api('state'));
      expect(publicState).not.toContain('synthetic_browser_access_'); expect(publicState).not.toContain('synthetic_browser_refresh_');
      section = await accounts(pane);
      await agentSection(pane, 'Connection');
      await section.getByRole('combobox', { name: 'Active account', exact: true }).selectOption(alpha.id);
      await expect(section.getByLabel('Account label', { exact: true })).toHaveValue('Alpha saved');
      fixture.state.revocationStatus = 400;
      await agentSection(pane, 'Connection');
      await section.getByRole('button', { name: 'Sign out and stop account tasks' }).click();
      await expect(section).toContainText('Remote revocation was not confirmed');
      expect(fixture.state.revocations[0].token).toBe('synthetic_browser_refresh_1');
      const state = (await api('state')).chatGpt;
      expect(state.accounts.find(account => account.id === alpha.id).signedIn).toBe(false);
      expect(state.accounts.find(account => account.id === beta.id).signedIn).toBe(true);
      await agentSection(pane, 'Connection');
      await section.getByRole('combobox', { name: 'Active account', exact: true }).selectOption(beta.id);
      expect((await api('state')).tasks.find(item => item.id === task).account.id).toBe(alpha.id);
    }, { accountStore: directory, handleHttp: fixture.handleHttp });
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test('ChatGPT account UI retries registration grants plan consent and sign-out stops an active response', async ({ page, request, baseURL }) => {
  const directory = await mkdtemp(join(tmpdir(), 'xamlg-account-browser-'));
  const fixture = accountServer(); fixture.state.plan = false; fixture.state.failToken = true;
  let closed = false;
  try {
    await withAgentWorkbench({ page, request, baseURL }, async ({ response }) => {
      response.once('close', () => { closed = true; }); sendAgentEvent(response, agentDelta('Account response still running'));
    }, async ({ pane, api }) => {
      await addAccount(page, pane, 'Consent account');
      await expect.poll(async () => (await api('state')).chatGpt.signIn.status).toBe('failed');
      let section = await accounts(pane);
      await expect(section.getByRole('button', { name: 'Retry registration sign-in' })).toBeVisible();
      await popupSignIn(page, section.getByRole('button', { name: 'Retry registration sign-in' }));
      const account = await signedIn(api, pane); expect(account.planEnabled).toBe(false);
      expect(fixture.state.authorizations[0].client_id).toBe('dynamic_agent_client');
      expect(fixture.state.authorizations[1].client_id).toBe(account.clientId);
      await agentSection(pane, 'Tasks');
      await expect(pane.getByRole('button', { name: 'Create task', exact: true })).toBeDisabled();
      section = await accounts(pane); fixture.state.plan = true;
      await popupSignIn(page, section.getByRole('button', { name: 'Enable ChatGPT plan usage' }));
      expect((await signedIn(api, pane)).id).toBe(account.id);
      expect(fixture.state.authorizations.at(-1).prompt).toBe('consent');
      const task = await createAgentTask(pane, 'Account cancellation', 'fixture-z');
      await agentSection(pane, 'Conversation');
      await pane.getByLabel('Message', { exact: true }).fill('Start a response.');
      await agentSection(pane, 'Conversation');
      await pane.getByRole('button', { name: 'Send message', exact: true }).click();
      await page.getByRole('dialog', { name: 'Review agent run' }).getByRole('button', { name: 'Confirm run', exact: true }).click();
      await expect(pane).toContainText('Account response still running');
      section = await accounts(pane);
      await agentSection(pane, 'Connection');
      await section.getByRole('button', { name: 'Sign out and stop account tasks' }).click();
      await expect.poll(() => closed).toBe(true);
      await expect.poll(async () => (await api('state')).tasks.find(item => item.id === task).status).toBe('paused');
      await agentSection(pane, 'Conversation');
      await expect(pane.locator('.agent-assistant_incomplete')).toContainText('Account response still running');
      const state = await api('state'); expect(state.chatGpt.accounts[0].signedIn).toBe(false);
      expect(fixture.state.revocations.at(-1).token).toBe('synthetic_browser_refresh_2');
      section = await accounts(pane);
      const newAccount = section.locator('details').filter({ has: page.locator('summary', { hasText: 'Add another account or workspace' }) });
      if (await newAccount.getAttribute('open') === null) await newAccount.locator('summary').click();
      const popup = await popupSignIn(page, newAccount.getByRole('button', { name: 'Continue with ChatGPT' }), false);
      await (await accounts(pane)).getByRole('button', { name: 'Cancel sign-in', exact: true }).click();
      await expect.poll(async () => (await api('state')).chatGpt.signIn.status).toBe('cancelled');
      await popup.close(); expect((await api('state')).chatGpt.accounts).toHaveLength(1);
    }, { accountStore: directory, handleHttp: fixture.handleHttp });
  } finally { await rm(directory, { recursive: true, force: true }); }
});
